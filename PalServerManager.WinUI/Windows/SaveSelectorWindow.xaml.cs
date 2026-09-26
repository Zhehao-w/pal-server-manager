using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;
using HaoHaoTianTian.PalHR.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR.Windows;

public sealed partial class SaveSelectorWindow : Window
{
    private readonly TaskCompletionSource<StartupChoice?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppWindow _appWindow;
    private bool _initialized;
    private bool _completed;
    private readonly WorldSettingsService _worldSettings;
    private readonly SaveSlotService _slots;
    private readonly WorldDiscoveryService _discovery;
    private readonly WorldImportService _importer;

    public SaveSelectorViewModel ViewModel { get; }

    public SaveSelectorWindow(SaveSelectorViewModel viewModel, WorldSettingsService worldSettings,
        SaveSlotService slots, WorldDiscoveryService discovery, WorldImportService importer)
    {
        ViewModel = viewModel;
        _worldSettings = worldSettings;
        _slots = slots;
        _discovery = discovery;
        _importer = importer;
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

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        ViewModel.IsBusy = true;
        try
        {
            var registry = await _slots.LoadAsync();
            var report = await _discovery.ScanAsync(registry);
            if (!report.HasIssues)
            {
                ViewModel.Feedback = "存档与登记一致。";
                return;
            }
            var candidate = await ShowDiscoveryAsync(report);
            if (candidate is null) return;
            var request = await ChooseImportAsync(registry, candidate);
            if (request is null) return;
            var imported = await _importer.ImportAsync(candidate.FolderName, candidate.WorldUid!, request.Value.Tag, request.Value.Source);
            await ViewModel.RefreshSavesAsync(imported.Id);
            ViewModel.Feedback = $"已导入存档 {imported.Id} · {imported.Tag}；服务器没有自动启动。";
        }
        catch (Exception error) { ViewModel.Feedback = "扫描或导入失败：" + error.Message; }
        finally { ViewModel.IsBusy = false; }
    }

    private async Task<WorldObservation?> ShowDiscoveryAsync(WorldDiscoveryReport report)
    {
        var list = new ListView { MinWidth = 790, MaxHeight = 390, SelectionMode = ListViewSelectionMode.Single };
        foreach (var observation in report.Worlds)
        {
            var status = observation.Status switch
            {
                WorldDiscoveryStatus.RegisteredHealthy => "已登记 · 正常",
                WorldDiscoveryStatus.RegisteredMissing => "已登记 · 缺失",
                WorldDiscoveryStatus.RegisteredIdentityMismatch => "已登记 · UID 不符",
                WorldDiscoveryStatus.RegisteredIncomplete => "已登记 · 不完整",
                WorldDiscoveryStatus.Importable => "可导入",
                WorldDiscoveryStatus.DuplicateUid => "重复 UID",
                WorldDiscoveryStatus.Incomplete => "不完整",
                _ => "忽略"
            };
            var detail = observation.Reason + (observation.HasWorldOption
                ? " 此世界包含 WorldOption.sav / WorldOptions.sav，游戏内设置可能覆盖部分服务器设置。" : "") +
                (observation.UnusualFolderName ? " 文件夹名称不是管理器标准格式；导入时仍保持原名。" : "");
            list.Items.Add(new ListViewItem
            {
                Tag = observation,
                Content = new StackPanel { Spacing = 2, Children =
                {
                    new TextBlock { Text = $"{status}   |   {observation.FolderName}   |   UID {observation.WorldUid ?? "—"}   |   编号 {(observation.SlotId?.ToString() ?? "—")}",
                        TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["PalMutedTextBrush"], FontSize = 11 }
                } }
            });
        }
        foreach (var profile in report.Profiles.Where(profile => profile.Status != WorldProfileStatus.ProfileHealthy))
            list.Items.Add(new ListViewItem { Content = new StackPanel { Spacing = 2, Children =
            {
                new TextBlock { Text = $"世界设置 · {profile.Status}   |   {System.IO.Path.GetFileName(profile.Path)}" },
                new TextBlock { Text = profile.Reason, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["PalMutedTextBrush"], FontSize = 11 }
            } } });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "重新扫描 · 只读结果",
            Content = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock { Text = $"发现 {report.ImportableCount} 个可导入世界。扫描不会修改存档或管理状态。仅可导入的未登记世界能执行导入。", TextWrapping = TextWrapping.Wrap },
                list
            } },
            SecondaryButtonText = "导入所选…",
            IsSecondaryButtonEnabled = false,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            MinWidth = 830
        };
        list.SelectionChanged += (_, _) => dialog.IsSecondaryButtonEnabled =
            (list.SelectedItem as ListViewItem)?.Tag is WorldObservation { CanImport: true };
        return await dialog.ShowAsync() == ContentDialogResult.Secondary
            ? (list.SelectedItem as ListViewItem)?.Tag as WorldObservation : null;
    }

    private async Task<(string Tag, ExistingWorldSettingsChoice Source)?> ChooseImportAsync(
        SaveSlotRegistry registry, WorldObservation candidate)
    {
        var defaultTag = candidate.FolderName.Contains(" - ", StringComparison.Ordinal)
            ? candidate.FolderName.Split(" - ", 3).Last() : candidate.FolderName;
        var tag = new TextBox { Header = "管理标签", Text = defaultTag.Length > 40 ? defaultTag[..40] : defaultTag, MaxLength = 40 };
        var sources = new[]
        {
            new ImportSourceOption(ExistingWorldSettingsMode.ActiveProfile, "复制当前存档的设置（推荐）"),
            new ImportSourceOption(ExistingWorldSettingsMode.OtherProfile, "复制另一已登记存档的设置"),
            new ImportSourceOption(ExistingWorldSettingsMode.CurrentServerIni, "使用当前服务器 PalWorldSettings.ini"),
            new ImportSourceOption(ExistingWorldSettingsMode.GameDefaults, "使用当前游戏默认设置"),
            new ImportSourceOption(ExistingWorldSettingsMode.Custom, "自定义世界设置…"),
            new ImportSourceOption(ExistingWorldSettingsMode.ExternalIni, "从其他 PalWorldSettings.ini 导入…")
        };
        var source = new ComboBox { Header = "世界设置来源", ItemsSource = sources, DisplayMemberPath = nameof(ImportSourceOption.Label),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var otherSlots = registry.Slots.OrderBy(slot => slot.Id).ToArray();
        var sourceSlot = new ComboBox { Header = "来源存档", ItemsSource = otherSlots, DisplayMemberPath = nameof(SaveSlot.Tag),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        source.SelectionChanged += (_, _) => sourceSlot.Visibility = (source.SelectedItem as ImportSourceOption)?.Mode ==
            ExistingWorldSettingsMode.OtherProfile ? Visibility.Visible : Visibility.Collapsed;
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(new TextBlock { Text = $"文件夹：{candidate.FolderName}\n世界 UID：{candidate.WorldUid}", TextWrapping = TextWrapping.Wrap });
        form.Children.Add(tag); form.Children.Add(source); form.Children.Add(sourceSlot);
        form.Children.Add(new TextBlock { Text = "只登记现有文件夹，不移动或修改游戏存档；不会改动 ServerName、密码、端口或其他全局设置。",
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["PalMutedTextBrush"] });
        if (candidate.HasWorldOption)
            form.Children.Add(new TextBlock { Text = "此世界包含 WorldOption.sav，游戏内世界设置可能覆盖部分服务器设置。",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["PalMintBrush"] });
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "导入现有存档", Content = form,
            PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, MinWidth = 520 };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var validatedTag = SaveSlotService.ValidateTag(tag.Text);
        var selectedMode = ((ImportSourceOption)source.SelectedItem).Mode;
        ExistingWorldSettingsChoice choice;
        if (selectedMode == ExistingWorldSettingsMode.Custom)
        {
            var active = registry.Slots.Single(slot => slot.Id == registry.ActiveSlotId);
            var session = await _worldSettings.CreateDraftSessionAsync(NewWorldSettingsMode.CopyCurrent, active, null);
            var result = await WorldSettingsEditorDialog.ShowAsync(RootGrid.XamlRoot, session, _worldSettings,
                serverRunning: false, allowRestart: false, includeGlobalSettings: false, existingWorldImport: true);
            if (result is null) return null;
            choice = new(selectedMode, CustomValues: result.ProfileValues);
        }
        else if (selectedMode == ExistingWorldSettingsMode.ExternalIni)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".ini");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return null;
            choice = new(selectedMode, ExternalIniPath: file.Path);
        }
        else choice = new(selectedMode, SourceSlotId: selectedMode == ExistingWorldSettingsMode.OtherProfile
            ? (sourceSlot.SelectedItem as SaveSlot)?.Id : null);
        if (!await ShowConfirmationAsync("确认导入", $"将登记文件夹 {candidate.FolderName}\nUID：{candidate.WorldUid}\n标签：{validatedTag}\n设置来源：{((ImportSourceOption)source.SelectedItem).Label}\n\n不会移动或改写游戏存档，也不会修改全局服务器设置。继续吗？")) return null;
        return (validatedTag, choice);
    }

    private sealed record ImportSourceOption(ExistingWorldSettingsMode Mode, string Label);

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
