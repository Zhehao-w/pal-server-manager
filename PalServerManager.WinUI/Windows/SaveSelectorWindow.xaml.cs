using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;
using HaoHaoTianTian.PalHR.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR.Windows;

public sealed partial class SaveSelectorWindow : Window
{
    private readonly TaskCompletionSource<StartupChoice?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppWindow _appWindow;
    private bool _initialized;
    private bool _completed;

    public SaveSelectorViewModel ViewModel { get; }

    public SaveSelectorWindow(SaveSelectorViewModel viewModel, WorldSettingsService worldSettings)
    {
        ViewModel = viewModel;
        InitializeComponent();
        VersionRun.Text = $"v{App.Version}";
        Title = $"{App.ProductName} · 选择存档并启动 · v{App.Version}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        SystemBackdrop = new MicaBackdrop();

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureTitleBar(_appWindow.TitleBar);
        WindowPlacement.CenterOnPrimaryDisplay(_appWindow, windowId, 940, 700);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }

        ViewModel.ConfirmAsync = ShowConfirmationAsync;
        ViewModel.ChooseBackupAsync = ShowBackupChoiceAsync;
        ViewModel.EditWorldSettingsAsync = session =>
            WorldSettingsEditorDialog.ShowAsync(RootGrid.XamlRoot, session, worldSettings, serverRunning: false, allowRestart: false);
        ViewModel.Completed += (_, choice) => Complete(choice);
        Activated += SaveSelectorWindow_Activated;
        Closed += (_, _) =>
        {
            if (!_completed) _completion.TrySetResult(null);
        };
    }

    public Task<StartupChoice?> ShowAsync()
    {
        Activate();
        return _completion.Task;
    }

    public void Dismiss()
    {
        if (!_completed) _completed = true;
        Close();
    }

    private async void SaveSelectorWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    private void Complete(StartupChoice? choice)
    {
        if (_completed) return;
        _completed = true;
        if (choice is null)
        {
            _completion.TrySetResult(null);
            Close();
            return;
        }

        // Keep this visible WinUI window alive while App creates the manager.
        // Closing or hiding the application's last visible window here can
        // terminate the process before the ShowAsync continuation runs.
        _completion.TrySetResult(choice);
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

    private void SaveList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.StartSelectedCommand.CanExecute(null)) ViewModel.StartSelectedCommand.Execute(null);
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = title == "删除存档" ? "删除存档" : "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<BackupEntry?> ShowBackupChoiceAsync(IReadOnlyList<BackupEntry> backups)
    {
        if (backups.Count == 0)
        {
            var empty = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "没有可用备份",
                Content = "当前 active 存档没有找到完整备份。",
                CloseButtonText = "知道了"
            };
            await empty.ShowAsync();
            return null;
        }
        var list = new ListView
        {
            ItemsSource = backups,
            DisplayMemberPath = nameof(BackupEntry.DisplayText),
            SelectedIndex = 0,
            MinWidth = 600,
            MaxHeight = 350
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "只回档当前 active 存档。默认仅恢复世界存档数据，当前世界设置不会随回档改变；恢复前会创建保护快照。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        var restoreSettings = new CheckBox { Content = "同时恢复此快照记录的世界设置", IsEnabled = (list.SelectedItem as BackupEntry)?.HasManagerManifest == true };
        list.SelectionChanged += (_, _) =>
        {
            restoreSettings.IsEnabled = (list.SelectedItem as BackupEntry)?.HasManagerManifest == true;
            if (!restoreSettings.IsEnabled) restoreSettings.IsChecked = false;
        };
        panel.Children.Add(restoreSettings);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择回档版本",
            Content = panel,
            PrimaryButtonText = "回档并启动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not BackupEntry selected) return null;
        return selected with { RestoreWorldSettings = restoreSettings.IsChecked == true };
    }
}
