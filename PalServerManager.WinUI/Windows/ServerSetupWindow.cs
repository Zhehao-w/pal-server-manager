using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR.Windows;

// Setup is deliberately separate from MainWindow: no server operation is created until state is ready.
public sealed class ServerSetupWindow : Window
{
    private readonly ServerRegistryService _registry;
    private readonly ServerStateService _serverState;
    private readonly TaskCompletionSource<RegisteredServer?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _servers = new() { MinWidth = 430, PlaceholderText = "选择已登记的服务器" };
    private readonly ComboBox _candidates = new() { MinWidth = 430, PlaceholderText = "选择自动发现的 PalServer" };
    private readonly TextBox _name = new() { Header = "显示名称", PlaceholderText = "例如：本机 PalServer" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private readonly Button _continue = new() { Content = "进入管理器" };
    private readonly Button _attach = new() { Content = "接入现有世界" };
    private readonly Button _refresh = new() { Content = "重新检查" };
    private readonly Button _relocate = new() { Content = "重新定位" };
    private readonly Button _remove = new() { Content = "移除注册（保留状态）" };
    private readonly StackPanel _panel = new() { Spacing = 12, Padding = new Thickness(26) };
    private ServerRegistryDocument _document = new();
    private bool _busy;

    public ServerSetupWindow(ServerRegistryService registry, ServerStateService serverState)
    {
        _registry = registry;
        _serverState = serverState;
        Title = $"{App.ProductName} · 服务器设置 · v{App.Version}";
        SystemBackdrop = new MicaBackdrop();
        var id = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(id);
        Services.WindowPlacement.CenterOnPrimaryDisplay(appWindow, id, 760, 650);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 700;
            presenter.PreferredMinimumHeight = 590;
        }

        _panel.Children.Add(new TextBlock { Text = "连接 PalServer", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _panel.Children.Add(new TextBlock { Text = "管理器安装目录、服务器目录和每台服务器的管理状态彼此独立。这里不会修改游戏存档。", TextWrapping = TextWrapping.Wrap });
        _panel.Children.Add(_servers);
        var registryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var detect = new Button { Content = "自动发现" };
        var browse = new Button { Content = "选择 PalServer.exe" };
        var registerDetected = new Button { Content = "登记所选" };
        registryButtons.Children.Add(detect); registryButtons.Children.Add(registerDetected); registryButtons.Children.Add(browse);
        _panel.Children.Add(registryButtons);
        _panel.Children.Add(_candidates);
        _panel.Children.Add(_name);
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionButtons.Children.Add(_relocate); actionButtons.Children.Add(_remove); actionButtons.Children.Add(_continue);
        _panel.Children.Add(actionButtons);
        _panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 8, 0, 8) });
        _panel.Children.Add(new TextBlock { Text = "服务器与存档", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _panel.Children.Add(_status);
        _panel.Children.Add(_summary);
        var stateButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateButtons.Children.Add(_refresh); stateButtons.Children.Add(_attach);
        _panel.Children.Add(stateButtons);
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Closed += (_, _) => _completion.TrySetResult(null);
        _servers.SelectionChanged += async (_, _) => await RunAsync(AssessSelectedAsync);
        _candidates.SelectionChanged += (_, _) =>
        {
            if (_candidates.SelectedItem is string path && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Path.GetFileName(path);
        };
        detect.Click += async (_, _) => await RunAsync(() =>
        {
            _candidates.Items.Clear();
            foreach (var root in new ServerDiscoveryService().AutoDetect()) _candidates.Items.Add(root);
            _status.Text = _candidates.Items.Count == 0 ? "常见 Steam 位置中没有找到可用 PalServer。请选择 PalServer.exe。" : "请选择发现的安装，或手动浏览。";
            return Task.CompletedTask;
        });
        browse.Click += async (_, _) => await RunAsync(async () =>
        {
            var path = await PickExeAsync();
            if (path is not null) await RegisterAsync(path);
        });
        registerDetected.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_candidates.SelectedItem is not string path)
                throw new InvalidOperationException("请先选择自动发现的服务器。");
            await RegisterAsync(Path.Combine(path, "PalServer.exe"));
        });
        _relocate.Click += async (_, _) => await RunAsync(async () =>
        {
            var selected = SelectedServer(); if (selected is null) return;
            var path = await PickExeAsync(); if (path is null) return;
            await _registry.RelocateAsync(selected.Id, path);
            await RefreshAsync();
        });
        _remove.Click += async (_, _) => await RunAsync(async () =>
        {
            var selected = SelectedServer(); if (selected is null) return;
            if (!await ConfirmAsync("移除注册", "只删除 Servers.json 中的登记；每台服务器的状态文件与 PalServer 均保留。")) return;
            await _registry.RemoveRegistrationAsync(selected.Id);
            await RefreshAsync();
        });
        _continue.Click += (_, _) =>
        {
            var selected = SelectedServer();
            if (selected is not null) _completion.TrySetResult(selected);
        };
        _refresh.Click += async (_, _) => await RunAsync(AssessSelectedAsync);
        _attach.Click += async (_, _) => await RunAsync(async () =>
        {
            var selected = SelectedServer(); if (selected is null) return;
            var discovered = _serverState.DiscoverWorlds(selected);
            if (!await ConfirmAsync("接入现有 PalServer",
                $"将登记发现的 {discovered.Count} 个世界，并为它们建立管理设置。首次接入时，各世界的设置以当前 PalWorldSettings.ini 为起点，之后可分别调整。不会修改 SaveGames 或游戏配置。")) return;
            _status.Text = "正在建立并验证管理状态…";
            await Task.Run(() => _serverState.AttachAsync(selected));
            await AssessSelectedAsync();
            _completion.TrySetResult(selected);
        });
    }

    public Task<RegisteredServer?> ShowAsync() { Activate(); _ = RunAsync(RefreshAsync); return _completion.Task; }
    public void Dismiss() => Close();

    private async Task RegisterAsync(string exe)
    {
        var root = ServerRegistryService.ValidateSelectedExe(exe);
        var name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileName(root) : _name.Text.Trim();
        await _registry.RegisterFromExeAsync(exe, name);
        _name.Text = "";
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _document = await _registry.LoadAsync();
        _servers.Items.Clear();
        foreach (var server in _document.Servers) _servers.Items.Add($"{server.DisplayName} · {server.ServerRoot}");
        var index = _document.Servers.FindIndex(server => server.Id == _document.SelectedServerId);
        _servers.SelectedIndex = index;
        await AssessSelectedAsync();
    }

    private RegisteredServer? SelectedServer() => _servers.SelectedIndex >= 0 && _servers.SelectedIndex < _document.Servers.Count
        ? _document.Servers[_servers.SelectedIndex] : null;

    private async Task AssessSelectedAsync()
    {
        var server = SelectedServer();
        if (server is null)
        {
            _status.Text = "还没有已登记的服务器。先自动发现，或选择 PalServer.exe。";
            _summary.Text = "";
            _continue.IsEnabled = _attach.IsEnabled = _relocate.IsEnabled = _remove.IsEnabled = false;
            return;
        }
        await _registry.SelectAsync(server.Id);
        var result = await _serverState.AssessAsync(server);
        _status.Text = result.Message;
        _summary.Text = "管理器状态与游戏存档分开保存；接入不会改动 SaveGames。";
        _continue.IsEnabled = result.Kind == ServerStateKind.Ready;
        _attach.IsEnabled = false;
        if (result.Kind is ServerStateKind.NeedsInitialization or ServerStateKind.EmptyNoWorld)
        {
            var discovered = _serverState.DiscoverWorlds(server);
            _summary.Text = discovered.Count == 0
                ? "未找到现有世界。请先单独启动 PalServer.exe，待首个世界存盘后关服，再点“重新检查”。"
                : $"已发现 {discovered.Count} 个世界（含当前世界）；接入后可分别管理。";
            _attach.Content = $"接入 {discovered.Count} 个现有世界";
            _attach.IsEnabled = discovered.Count > 0;
        }
        _relocate.IsEnabled = _remove.IsEnabled = true;
    }

    private async Task<string?> PickExeAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = "确认", CloseButtonText = "取消", XamlRoot = _panel.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception error) { _status.Text = "操作未完成：" + error.Message; }
        finally { _busy = false; }
    }
}
