using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;
using HaoHaoTianTian.PalHR.ViewModels;
using HaoHaoTianTian.PalHR.Windows;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly StartupChoice? _startupChoice;
    private readonly AppWindow _appWindow;
    private bool _initialized;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, WorldSettingsService worldSettings, ServerManagerService manager, StartupChoice? startupChoice = null)
    {
        ViewModel = viewModel;
        _startupChoice = startupChoice;
        InitializeComponent();
        VersionRun.Text = $"v{App.Version}";
        Title = $"{App.ProductName} · v{App.Version}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        SystemBackdrop = new MicaBackdrop();

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureTitleBar(_appWindow.TitleBar);
        WindowPlacement.CenterOnPrimaryDisplay(_appWindow, windowId, 1040, 900);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 740;
        }

        ViewModel.ConfirmAsync = ShowConfirmationAsync;
        ViewModel.ChooseBackupAsync = ShowBackupChoiceAsync;
        ViewModel.ShowWorldSettingsEditorAsync = (session, running) =>
            WorldSettingsEditorDialog.ShowAsync(RootGrid.XamlRoot, session, worldSettings, running, allowRestart: true);
        manager.ResolveWorldOptionAsync = ShowWorldOptionConflictAsync;
        manager.ResolveUpdateProtectionAsync = ShowUpdateProtectionAsync;
        ViewModel.RequestClose += (_, _) =>
        {
            void CloseOnUiThread()
            {
                _allowClose = true;
                Close();
            }
            if (DispatcherQueue.HasThreadAccess) CloseOnUiThread();
            else DispatcherQueue.TryEnqueue(CloseOnUiThread);
        };
        _appWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private static void ConfigureTitleBar(AppWindowTitleBar titleBar)
    {
        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(150, 242, 247, 248);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(42, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(70, 255, 255, 255);
        titleBar.ButtonPressedForegroundColor = Colors.White;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (ViewModel.IsBusy)
        {
            args.Cancel = true;
            ViewModel.StatusMessage = "当前操作尚未完成，请等待完成后再关闭管理器。";
            return;
        }
        if (!ViewModel.HasSchedule) return;
        args.Cancel = true;
        if (!await ShowConfirmationAsync("取消预约并关闭管理器", "关闭管理器会取消当前预约，但不会关闭服务器。\n\n确定关闭吗？")) return;
        ViewModel.CancelScheduleCommand.Execute(null);
        _allowClose = true;
        Close();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        await ViewModel.InitializeAsync();
        if (_startupChoice is not null) await ViewModel.ExecuteStartupChoiceAsync(_startupChoice);
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<BackupEntry?> ShowBackupChoiceAsync(IReadOnlyList<BackupEntry> backups, bool serverRunning)
    {
        if (backups.Count == 0)
        {
            var empty = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "没有可用备份",
                Content = "当前存档没有找到完整的游戏自动备份或回档前保护副本。",
                CloseButtonText = "知道了"
            };
            await empty.ShowAsync();
            return null;
        }

        var list = new ListView
        {
            ItemsSource = backups,
            DisplayMemberPath = nameof(BackupEntry.DisplayText),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MinWidth = 600,
            MaxHeight = 360
        };
        var explanation = serverRunning
            ? "服务器正在运行：确认后会先保存并正常关服，恢复所选版本，再自动启动同一存档。"
            : "服务器已关闭：确认后直接覆盖当前存档；游戏自动备份与 WorldOption 设置会保留。";
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(list);
        content.Children.Add(new TextBlock { Text = "默认仅恢复世界存档数据；当前世界设置不会随回档改变。回档前会自动创建保护快照。", Opacity = 0.78, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var restoreSettings = new CheckBox { Content = "同时恢复此快照记录的世界设置", IsEnabled = (list.SelectedItem as BackupEntry)?.HasManagerManifest == true };
        list.SelectionChanged += (_, _) =>
        {
            restoreSettings.IsEnabled = (list.SelectedItem as BackupEntry)?.HasManagerManifest == true;
            if (!restoreSettings.IsEnabled) restoreSettings.IsChecked = false;
        };
        content.Children.Add(restoreSettings);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择回档版本",
            Content = content,
            PrimaryButtonText = serverRunning ? "保存、回档并重启" : "恢复此备份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not BackupEntry selected) return null;
        return selected with { RestoreWorldSettings = restoreSettings.IsChecked == true };
    }

    private async Task<WorldOptionDecision> ShowWorldOptionConflictAsync(WorldOptionConflict conflict)
    {
        var folder = new Button { Content = "打开所在文件夹", HorizontalAlignment = HorizontalAlignment.Left };
        folder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{conflict.FilePath}\"") { UseShellExecute = true })?.Dispose();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"检测到 {System.IO.Path.GetFileName(conflict.FilePath)}。它可能覆盖 PalWorldSettings.ini，使游戏实际设置与管理器中保存的设置不同。", TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        panel.Children.Add(folder);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "检测到世界本地设置文件", Content = panel, PrimaryButtonText = "备份并停用后启动", CloseButtonText = "取消启动", DefaultButton = ContentDialogButton.Close };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return WorldOptionDecision.Cancel;
        return await ShowConfirmationAsync("确认备份并停用", "原文件会被可逆地重命名并保留，不会删除。确定继续吗？") ? WorldOptionDecision.BackupAndDisable : WorldOptionDecision.Cancel;
    }

    private async Task<UpdateProtectionDecision> ShowUpdateProtectionAsync(SaveSlot slot, string build)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "检测到新的服务器版本",
            Content = new TextBlock { Text = $"存档 {slot.Id} · {slot.Tag} 尚未在当前 Palworld Dedicated Server 版本下成功启动。\n\n建议在首次启动前创建完整保护快照（包括维度帕鲁终端数据）。\n版本标识：{build}", TextWrapping = TextWrapping.Wrap, MaxWidth = 580 },
            PrimaryButtonText = "创建快照并启动",
            SecondaryButtonText = "仅启动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => UpdateProtectionDecision.SnapshotAndStart, ContentDialogResult.Secondary => UpdateProtectionDecision.StartOnly, _ => UpdateProtectionDecision.Cancel };
    }

    private void CopyUid_Click(object sender, RoutedEventArgs e)
    {
        var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ViewModel.WorldUid);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ViewModel.StatusMessage = "存档 UID 已复制。";
    }

    private void IncreaseIdleMinutes_Click(object sender, RoutedEventArgs e) =>
        ViewModel.IdleShutdownMinutes = NormalizeIdleMinutes(ViewModel.IdleShutdownMinutes) + 1;

    private void DecreaseIdleMinutes_Click(object sender, RoutedEventArgs e) =>
        ViewModel.IdleShutdownMinutes = NormalizeIdleMinutes(ViewModel.IdleShutdownMinutes) - 1;

    private static double NormalizeIdleMinutes(double value) =>
        Math.Clamp(double.IsFinite(value) ? Math.Round(value) : 15, 2, 1439);
}
