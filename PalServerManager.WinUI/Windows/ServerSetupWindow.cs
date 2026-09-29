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
    private readonly FirstRunServerSetupService _firstRunSetup;
    private readonly TaskCompletionSource<RegisteredServer?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _servers = new() { MinWidth = 430, PlaceholderText = "选择已登记的服务器", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _candidates = new() { MinWidth = 430, PlaceholderText = "选择自动发现的 PalServer", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Foreground = Brush("PalTextBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Foreground = Brush("PalMutedTextBrush"), FontSize = 11 };
    private readonly Button _continue = new() { Content = "进入管理器", Style = Style("PrimaryPalButtonStyle") };
    private readonly Button _attach = new() { Content = "接入并继续", Style = Style("PrimaryPalButtonStyle") };
    private readonly Button _refresh = new() { Content = "重新检查", Style = Style("CompactPalButtonStyle") };
    private readonly Button _relocate = new() { Content = "重新定位", Style = Style("CompactPalButtonStyle") };
    private readonly Button _remove = new() { Content = "移除注册（保留状态）", Style = Style("CompactPalButtonStyle") };
    private readonly StackPanel _panel = new() { Spacing = 16, Padding = new Thickness(28, 18, 28, 26) };
    private readonly StackPanel _registryButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _managementButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _useDetected = new() { Content = "使用此服务器", Style = Style("CompactPalButtonStyle") };
    private ServerRegistryDocument _document = new();
    private string? _pendingRoot;
    private bool _busy;
    private bool _dpiWatcherAttached;
    private double _lastRasterizationScale;

    public ServerSetupWindow(ServerRegistryService registry, ServerStateService serverState, FirstRunServerSetupService firstRunSetup)
    {
        _registry = registry;
        _serverState = serverState;
        _firstRunSetup = firstRunSetup;
        Title = $"{App.ProductName} · 服务器设置 · v{App.Version}";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        var id = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(id);
        ConfigureTitleBar(appWindow.TitleBar);
        Services.WindowPlacement.CenterOnPrimaryDisplay(appWindow, id, 760, 650);
        ApplyResponsiveMinimum(appWindow, id);

        var root = new Grid { Background = Brush("PalWindowTintBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Grid
        {
            Background = Brush("PalTitleBarBrush"),
            Padding = new Thickness(18, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/ManagerIconCircular.png")), Width = 24, Height = 24 });
        titleStack.Children.Add(new TextBlock { Text = App.ProductName, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        titleStack.Children.Add(new TextBlock { Text = "·", Foreground = Brush("PalMutedTextBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        titleStack.Children.Add(new TextBlock { Text = $"v{App.Version}", Foreground = Brush("PalMutedTextBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        titleBar.Children.Add(titleStack);
        root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var heading = new StackPanel { Spacing = 3, Margin = new Thickness(2, 0, 2, 2) };
        heading.Children.Add(new TextBlock { Text = "连接 PalServer", FontSize = 23, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "选择或登记服务器，然后验证当前 SaveGames\\0。接入不会修改游戏存档。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("PalMutedTextBrush"), FontSize = 11 });
        _panel.Children.Add(heading);

        var detect = new Button { Content = "自动发现", Style = Style("CompactPalButtonStyle") };
        var browse = new Button { Content = "选择 PalServer.exe", Style = Style("CompactPalButtonStyle") };
        _registryButtons.Children.Add(detect);
        _registryButtons.Children.Add(_useDetected);
        _registryButtons.Children.Add(browse);
        _managementButtons.Children.Add(_continue);
        _managementButtons.Children.Add(_relocate);
        _managementButtons.Children.Add(_remove);

        _panel.Children.Add(SectionLabel("服务器"));
        var serverGroup = new StackPanel { Spacing = 10, Padding = new Thickness(14, 12, 14, 12) };
        serverGroup.Children.Add(_servers);
        serverGroup.Children.Add(_candidates);
        serverGroup.Children.Add(_registryButtons);
        serverGroup.Children.Add(_managementButtons);
        _panel.Children.Add(Group(serverGroup));

        _panel.Children.Add(SectionLabel("服务器与存档"));
        var stateButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateButtons.Children.Add(_attach);
        stateButtons.Children.Add(_refresh);
        var stateGroup = new StackPanel { Spacing = 10, Padding = new Thickness(14, 12, 14, 12) };
        stateGroup.Children.Add(_status);
        stateGroup.Children.Add(new Border { Height = 1, Background = Brush("PalHairlineBrush"), Margin = new Thickness(0, 2, 0, 2) });
        stateGroup.Children.Add(_summary);
        stateGroup.Children.Add(stateButtons);
        _panel.Children.Add(Group(stateGroup));

        var rootScroller = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(rootScroller, 1);
        root.Children.Add(rootScroller);
        Content = root;
        rootScroller.Loaded += (_, _) =>
        {
            if (_dpiWatcherAttached) return;
            _dpiWatcherAttached = true;
            var xamlRoot = rootScroller.XamlRoot;
            _lastRasterizationScale = xamlRoot.RasterizationScale;
            ApplyResponsiveMinimum(appWindow, id);
            xamlRoot.Changed += (_, _) =>
            {
                var scale = xamlRoot.RasterizationScale;
                if (Math.Abs(scale - _lastRasterizationScale) < 0.001) return;
                _lastRasterizationScale = scale;
                ApplyResponsiveMinimum(appWindow, id);
            };
            appWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange) ApplyResponsiveMinimum(appWindow, id);
            };
        };

        Closed += (_, _) => _completion.TrySetResult(null);
        _servers.SelectionChanged += async (_, _) => await RunAsync(PersistSelectionAndAssessAsync);
        _candidates.SelectionChanged += async (_, _) => await RunAsync(SelectCandidateAsync);
        detect.Click += async (_, _) => await RunAsync(async () =>
        {
            _candidates.Items.Clear();
            foreach (var rootPath in new ServerDiscoveryService().AutoDetect()) _candidates.Items.Add(rootPath);
            _candidates.Visibility = _candidates.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_document.Servers.Count > 0)
            {
                if (_candidates.Items.Count == 1) _candidates.SelectedIndex = 0;
                await AssessSelectedAsync();
                return;
            }
            if (_candidates.Items.Count == 1)
            {
                _candidates.SelectedIndex = 0;
                await SelectCandidateAsync();
                return;
            }
            _pendingRoot = null;
            _status.Text = _candidates.Items.Count == 0 ? "常见 Steam 位置中没有找到可用 PalServer。请选择 PalServer.exe。" : "请选择发现的 PalServer 安装。";
            _summary.Text = "";
            UpdateFirstRunActions(hasWorld: false);
        });
        browse.Click += async (_, _) => await RunAsync(async () =>
        {
            var path = await PickExeAsync();
            if (path is null) return;
            if (_document.Servers.Count == 0) await SelectPendingAsync(path);
            else await RegisterAsync(path);
        });
        _useDetected.Click += async (_, _) => await RunAsync(async () =>
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
            if (_document.Servers.Count == 0)
            {
                if (_pendingRoot is null) return;
                var exe = Path.Combine(_pendingRoot, "PalServer.exe");
                var name = string.IsNullOrWhiteSpace(Path.GetFileName(_pendingRoot)) ? "PalServer" : Path.GetFileName(_pendingRoot);
                _status.Text = "正在登记服务器并建立、验证管理状态…";
                var registered = await _firstRunSetup.RegisterAndAttachAsync(exe, name);
                _completion.TrySetResult(registered);
                return;
            }
            var selected = SelectedServer(); if (selected is null) return;
            var discovered = _serverState.DiscoverWorlds(selected);
            if (!await ConfirmAsync("接入现有 PalServer",
                $"将只接入当前 SaveGames\\0 的 {discovered.Count} 个世界，并以当前 PalWorldSettings.ini 初始化它的管理设置。其他世界以后可使用“导入存档…”加入。接入不会修改 SaveGames 或游戏配置。")) return;
            _status.Text = "正在建立并验证管理状态…";
            await Task.Run(() => _serverState.AttachAsync(selected));
            await AssessSelectedAsync();
            _completion.TrySetResult(selected);
        });
    }

    public Task<RegisteredServer?> ShowAsync() { Activate(); _ = RunAsync(RefreshAsync); return _completion.Task; }
    public void Dismiss() => Close();

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush("PalMutedTextBrush"),
        Margin = new Thickness(8, 0, 0, -8)
    };

    private static Border Group(UIElement child) => new()
    {
        Style = Style("GroupedSurfaceStyle"),
        Child = child
    };

    private static void ConfigureTitleBar(AppWindowTitleBar titleBar)
    {
        var foreground = global::Windows.UI.Color.FromArgb(255, 28, 28, 30);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(120, 28, 28, 30);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(18, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(30, 0, 0, 0);
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static void ApplyResponsiveMinimum(AppWindow appWindow, WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        if (appWindow.Presenter is not OverlappedPresenter presenter) return;
        presenter.PreferredMinimumWidth = Math.Min(Services.WindowPlacement.EffectivePixelsToPhysical(windowId, 700), workArea.Width);
        presenter.PreferredMinimumHeight = Math.Min(Services.WindowPlacement.EffectivePixelsToPhysical(windowId, 590), workArea.Height);
    }

    private async Task RegisterAsync(string exe)
    {
        var root = ServerRegistryService.ValidateSelectedExe(exe);
        var name = string.IsNullOrWhiteSpace(Path.GetFileName(root)) ? "PalServer" : Path.GetFileName(root);
        await _registry.RegisterFromExeAsync(exe, name);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _document = await _registry.LoadAsync();
        _servers.Items.Clear();
        foreach (var server in _document.Servers) _servers.Items.Add($"{server.DisplayName} · {server.ServerRoot}");
        var index = _document.Servers.FindIndex(server => server.Id == _document.SelectedServerId);
        _servers.SelectedIndex = index;
        var firstRun = _document.Servers.Count == 0;
        _servers.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        _managementButtons.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        _useDetected.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        _candidates.Visibility = firstRun && _candidates.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        await AssessSelectedAsync();
    }

    private RegisteredServer? SelectedServer() => _servers.SelectedIndex >= 0 && _servers.SelectedIndex < _document.Servers.Count
        ? _document.Servers[_servers.SelectedIndex] : null;

    private async Task PersistSelectionAndAssessAsync()
    {
        var server = SelectedServer();
        if (server is not null) await _registry.SelectAsync(server.Id);
        await AssessSelectedAsync();
    }

    private async Task AssessSelectedAsync()
    {
        var server = SelectedServer();
        if (server is null)
        {
            if (_pendingRoot is not null) { await AssessPendingAsync(); return; }
            _status.Text = "还没有已登记的服务器。请自动发现，或选择 PalServer.exe。";
            _summary.Text = "选择后将在这里检查服务器与现有 SaveGames\\0。";
            _continue.Visibility = _attach.Visibility = Visibility.Collapsed;
            _continue.IsEnabled = _attach.IsEnabled = _relocate.IsEnabled = _remove.IsEnabled = false;
            _refresh.Visibility = Visibility.Collapsed;
            return;
        }
        var result = await _serverState.AssessAsync(server);
        _status.Text = result.Message;
        _summary.Text = "管理器状态与游戏存档分开保存；接入不会改动 SaveGames。";
        _continue.IsEnabled = result.Kind == ServerStateKind.Ready;
        _continue.Visibility = result.Kind == ServerStateKind.Ready ? Visibility.Visible : Visibility.Collapsed;
        _attach.Visibility = Visibility.Collapsed;
        _attach.IsEnabled = false;
        if (result.Kind is ServerStateKind.NeedsInitialization or ServerStateKind.EmptyNoWorld)
        {
            var discovered = _serverState.DiscoverWorlds(server);
            _summary.Text = discovered.Count == 0
                ? "未找到现有世界。请先单独启动 PalServer.exe，待首个世界存盘后关服，再点“重新检查”。"
                : $"1. 已选择服务器\n   {server.ServerRoot}\n\n2. 已发现当前存档\n   SaveGames\\0\n   UID: {discovered.Single().WorldGuid}\n\n接入会使用当前 PalWorldSettings.ini 初始化世界设置，不会移动 SaveGames\\0。";
            _attach.Content = "接入并继续";
            _attach.IsEnabled = discovered.Count > 0;
            _attach.Visibility = discovered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _relocate.IsEnabled = _remove.IsEnabled = true;
        _refresh.Visibility = Visibility.Visible;
    }

    private Task SelectCandidateAsync()
    {
        if (_document.Servers.Count != 0) return Task.CompletedTask;
        if (_candidates.SelectedItem is not string root) return Task.CompletedTask;
        return SelectPendingAsync(Path.Combine(root, "PalServer.exe"));
    }

    private async Task SelectPendingAsync(string exe)
    {
        _pendingRoot = ServerRegistryService.ValidateSelectedExe(exe);
        if (!_candidates.Items.Cast<object>().Any(item => string.Equals(item?.ToString(), _pendingRoot, StringComparison.OrdinalIgnoreCase)))
            _candidates.Items.Add(_pendingRoot);
        _candidates.SelectedItem = _pendingRoot;
        _candidates.Visibility = Visibility.Visible;
        await AssessPendingAsync();
    }

    private Task AssessPendingAsync()
    {
        if (_pendingRoot is null) return Task.CompletedTask;
        UpdateFirstRunActions(hasWorld: false);
        var pending = RegisteredServer.Create("pending", _pendingRoot);
        IReadOnlyList<DiscoveredWorld> worlds;
        try { worlds = _serverState.DiscoverWorlds(pending); }
        catch (Exception error)
        {
            _status.Text = $"当前 SaveGames\\0 暂时无法接入：{error.Message}\n完成存盘或修复问题后请重新检查。";
            _summary.Text = $"已选择服务器\n   {_pendingRoot}";
            return Task.CompletedTask;
        }
        _status.Text = worlds.Count == 0
            ? "PalServer 路径有效，但未找到可接入的现有世界。"
            : "PalServer 路径有效，已找到可接入的现有世界。";
        _summary.Text = worlds.Count == 0
            ? $"已选择服务器\n   {_pendingRoot}\n\n未找到现有世界。请先单独启动 PalServer.exe，待首个世界存盘后关服，再点“重新检查”。"
            : $"1. 已选择服务器\n   {_pendingRoot}\n\n2. 已发现当前存档\n   SaveGames\\0\n   UID: {worlds.Single().WorldGuid}\n\n接入会登记这台服务器，并使用当前 PalWorldSettings.ini 初始化世界设置；不会移动 SaveGames\\0。";
        UpdateFirstRunActions(worlds.Count > 0);
        return Task.CompletedTask;
    }

    private void UpdateFirstRunActions(bool hasWorld)
    {
        _attach.Content = "接入并继续";
        _attach.IsEnabled = hasWorld;
        _attach.Visibility = hasWorld ? Visibility.Visible : Visibility.Collapsed;
        _refresh.Visibility = _pendingRoot is not null && !hasWorld ? Visibility.Visible : Visibility.Collapsed;
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
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Brush("PalTextBrush") },
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _panel.XamlRoot
        };
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
