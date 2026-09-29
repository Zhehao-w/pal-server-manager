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
    private IReadOnlyList<WorldObservation> _migrationCandidates = [];

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
        _appWindow.Closing += SaveSelectorWindow_Closing;
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

    private void SaveSelectorWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!ViewModel.IsBusy) return;
        args.Cancel = true;
        ViewModel.Feedback = "存档操作正在进行，请等待完成后再关闭窗口。";
    }

    private async void SaveSelectorWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        await ViewModel.InitializeAsync();
        try { await RefreshMigrationNoticeAsync(); }
        catch (Exception error)
        {
            MigrationNotice.Visibility = Visibility.Collapsed;
            ViewModel.Feedback = "未能检查其他现有存档：" + error.Message;
        }
    }

    private async Task RefreshMigrationNoticeAsync()
    {
        var registry = await _slots.LoadAsync();
        var report = await _discovery.ScanAsync(registry);
        _migrationCandidates = report.Worlds.Where(world => world.CanImport).ToArray();
        MigrationNoticeText.Text = $"发现 {_migrationCandidates.Count} 个现有存档尚未加入管理器";
        MigrationNotice.Visibility = _migrationCandidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ImportDiscovered_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || _migrationCandidates.Count == 0) return;
        ViewModel.IsBusy = true;
        try
        {
            var candidate = await ShowDiscoveryAsync(new WorldDiscoveryReport(_migrationCandidates, []));
            if (candidate is null) return;
            await ImportSelectedAsync(await _slots.LoadAsync(), [candidate]);
            await RefreshMigrationNoticeAsync();
        }
        catch (Exception error) { ViewModel.Feedback = "导入存档失败：" + error.Message; }
        finally { ViewModel.IsBusy = false; }
    }

    private void DismissMigrationNotice_Click(object sender, RoutedEventArgs e) =>
        MigrationNotice.Visibility = Visibility.Collapsed;

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
            await ImportSelectedAsync(registry, [candidate]);
        }
        catch (Exception error) { ViewModel.Feedback = "扫描或导入失败：" + error.Message; }
        finally { ViewModel.IsBusy = false; }
    }

    private async void ImportSave_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        ViewModel.IsBusy = true;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var registry = await _slots.LoadAsync();
            var report = await _discovery.ScanSourceAsync(folder.Path, registry);
            var selected = await ShowSourceChoiceAsync(report, folder.Path);
            if (selected.Count > 0) await ImportSelectedAsync(registry, selected);
        }
        catch (Exception error) { ViewModel.Feedback = "导入存档失败：" + error.Message; }
        finally { ViewModel.IsBusy = false; }
    }

    private async Task<IReadOnlyList<WorldObservation>> ShowSourceChoiceAsync(WorldDiscoveryReport report, string sourcePath)
    {
        var valid = report.Worlds.Where(world => world.CanImport).ToArray();
        if (valid.Length == 0)
        {
            var empty = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "没有可导入的存档",
                Content = new TextBlock { Text = $"所选目录：{sourcePath}\n未找到 UID 唯一且包含 Level.sav 和 LevelMeta.sav 的世界。不会递归搜索备份目录。",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }, CloseButtonText = "关闭" };
            await empty.ShowAsync();
            return [];
        }
        var list = new ListView { MinWidth = 620, MaxHeight = 360, SelectionMode = ListViewSelectionMode.Multiple };
        foreach (var world in valid)
            list.Items.Add(new ListViewItem { Tag = world,
                Content = new TextBlock { Text = $"{world.FolderName}   ·   {world.WorldUid}", TextTrimming = TextTrimming.CharacterEllipsis } });
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "选择要导入的存档",
            Content = new StackPanel { Spacing = 9, Children =
            {
                new TextBlock { Text = $"找到 {valid.Length} 个可导入世界。可多选；只扫描所选目录及其直接子目录。", TextWrapping = TextWrapping.Wrap }, list
            } }, PrimaryButtonText = "继续", IsPrimaryButtonEnabled = false, CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, MinWidth = 650 };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? list.SelectedItems.Cast<ListViewItem>().Select(item => (WorldObservation)item.Tag).ToArray() : [];
    }

    private async Task ImportSelectedAsync(SaveSlotRegistry registry, IReadOnlyList<WorldObservation> candidates)
    {
        var request = await ChooseImportAsync(registry, candidates);
        if (request is null) return;
        var imported = new List<WorldImportResult>();
        foreach (var candidate in candidates)
        {
            try
            {
                var tag = candidates.Count == 1 ? request.Value.SingleTag! : DefaultImportTag(candidate);
                imported.Add(await Task.Run(() =>
                    _importer.ImportAsync(candidate.FolderPath, candidate.WorldUid!, tag, request.Value.Source)));
            }
            catch (Exception error)
            {
                if (imported.Count > 0) await ViewModel.RefreshSavesAsync(imported[^1].Slot.Id);
                ViewModel.Feedback = $"已导入 {imported.Count} 个；后续导入失败：{error.Message}";
                return;
            }
        }
        await ViewModel.RefreshSavesAsync(imported[^1].Slot.Id);
        var warnings = imported.Where(item => item.CleanupWarning is not null).Select(item => item.CleanupWarning).ToArray();
        ViewModel.Feedback = $"已导入 {imported.Count} 个存档；服务器没有自动启动。" +
            (warnings.Length > 0 ? " " + string.Join(" ", warnings) : "");
    }

    private static string DefaultImportTag(WorldObservation candidate)
    {
        var name = candidate.FolderName;
        if (name.Length == 32 && name.All(Uri.IsHexDigit))
            name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(candidate.FolderPath))!;
        if (name is "0" or "SaveGames" || string.IsNullOrWhiteSpace(name)) name = "导入存档";
        if (name.Contains(" - ", StringComparison.Ordinal)) name = name.Split(" - ", 3).Last();
        return SaveSlotService.ValidateTag(name.Length > 40 ? name[..40] : name);
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
                ? " 此世界包含 WorldOption.sav / WorldOptions.sav；导入的副本会保留原字节并停用该覆盖文件。" : "") +
                (observation.UnusualFolderName ? " 导入时会复制到管理器规范目录，原文件夹不会登记。" : "");
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

    private async Task<(string? SingleTag, ExistingWorldSettingsChoice Source)?> ChooseImportAsync(
        SaveSlotRegistry registry, IReadOnlyList<WorldObservation> candidates)
    {
        var tag = new TextBox { Header = "管理标签", Text = candidates.Count == 1 ? DefaultImportTag(candidates[0]) : "", MaxLength = 40 };
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
        form.Children.Add(new TextBlock { Text = candidates.Count == 1
            ? $"来源：{candidates[0].FolderPath}\n世界 UID：{candidates[0].WorldUid}"
            : $"已选择 {candidates.Count} 个世界；各自标签取来源文件夹名称，之后可用“修改标签”调整。", TextWrapping = TextWrapping.Wrap });
        if (candidates.Count == 1) form.Children.Add(tag);
        form.Children.Add(source); form.Children.Add(sourceSlot);
        form.Children.Add(new TextBlock { Text = "将复制并校验世界文件，再登记规范停放目录。外部来源保持不变；当前 SaveGames 内的旧来源在成功登记后才会尝试清理。不会改动全局服务器设置。",
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["PalMutedTextBrush"] });
        if (candidates.Any(candidate => candidate.HasWorldOption))
            form.Children.Add(new TextBlock { Text = "检测到 WorldOption.sav / WorldOptions.sav：导入的副本会保留原字节并可逆地停用，外部来源不变。",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["PalMintBrush"] });
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "导入现有存档", Content = form,
            PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, MinWidth = 520 };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var validatedTag = candidates.Count == 1 ? SaveSlotService.ValidateTag(tag.Text) : null;
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
        if (!await ShowConfirmationAsync("确认导入", $"将复制并登记 {candidates.Count} 个世界到当前服务器的规范停放目录。\n设置来源：{((ImportSourceOption)source.SelectedItem).Label}\n\n外部来源不会被修改；当前 SaveGames 内的非规范来源仅在成功登记后清理。不会修改全局服务器设置。继续吗？")) return null;
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
