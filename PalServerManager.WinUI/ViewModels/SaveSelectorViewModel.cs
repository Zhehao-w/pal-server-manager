using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;

namespace HaoHaoTianTian.PalHR.ViewModels;

public partial class SaveSelectorViewModel(SaveSlotService slots, BackupService backups, WorldSettingsService worldSettings, DeleteWorldService deletion) : ObservableObject
{
    private SaveSlotRegistry? _registry;
    private Dictionary<string, string>? _customDraft;
    private Dictionary<string, string>? _customGlobals;

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<IReadOnlyList<BackupEntry>, Task<BackupEntry?>>? ChooseBackupAsync { get; set; }
    public Func<WorldSettingsEditorSession, Task<WorldSettingsEditorResult?>>? EditWorldSettingsAsync { get; set; }
    public event EventHandler<StartupChoice?>? Completed;

    public ObservableCollection<SaveSlotDisplay> Saves { get; } = [];
    public IReadOnlyList<WorldSettingsSourceOption> WorldSettingsSources { get; } =
    [
        new(NewWorldSettingsMode.CopyCurrent, "复制当前存档设置", "以当前存档的 profile 为起点（推荐）"),
        new(NewWorldSettingsMode.GameDefaults, "游戏默认设置", "官方默认值 + 便携的新世界默认覆盖"),
        new(NewWorldSettingsMode.Custom, "自定义设置…", "创建前打开世界设置编辑器")
    ];

    [ObservableProperty] public partial SaveSlotDisplay? SelectedSave { get; set; }
    [ObservableProperty] public partial string EditTag { get; set; } = "";
    [ObservableProperty] public partial string NewTag { get; set; } = "";
    [ObservableProperty] public partial string SelectedSaveText { get; set; } = "未选择";
    [ObservableProperty] public partial string Feedback { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial WorldSettingsSourceOption SelectedWorldSettingsSource { get; set; } =
        new(NewWorldSettingsMode.CopyCurrent, "复制当前存档设置", "以当前存档的 profile 为起点（推荐）");

    public async Task InitializeAsync()
    {
        try { await ReloadAsync(); }
        catch (Exception exception) { Feedback = $"读取存档失败：{exception.Message}"; }
    }

    partial void OnSelectedSaveChanged(SaveSlotDisplay? value)
    {
        EditTag = "";
        Feedback = "";
        SelectedSaveText = value is null ? "未选择" : $"{value.Id} · {value.Tag}";
        RenameTagCommand.NotifyCanExecuteChanged();
        StartSelectedCommand.NotifyCanExecuteChanged();
        EditSelectedWorldSettingsCommand.NotifyCanExecuteChanged();
        DeleteWorldCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditTagChanged(string value) => RenameTagCommand.NotifyCanExecuteChanged();

    private bool CanRenameTag() =>
        !IsBusy && SelectedSave is not null && !string.IsNullOrWhiteSpace(EditTag) &&
        !string.Equals(EditTag.Trim(), SelectedSave.Tag, StringComparison.Ordinal);

    private bool CanStartSelected() => !IsBusy && SelectedSave is not null;
    private bool CanDeleteWorld() => !IsBusy && SelectedSave is not null && _registry is not null && SelectedSave.Id != _registry.ActiveSlotId;

    [RelayCommand(CanExecute = nameof(CanDeleteWorld))]
    private async Task DeleteWorldAsync()
    {
        if (SelectedSave is null || ConfirmAsync is null) return;
        var selected = SelectedSave;
        if (!await ConfirmAsync("删除存档", $"编号：{selected.Id}\n标签：{selected.Tag}\n世界 UID：{selected.WorldUid}\n\n服务器必须已关闭，当前存档不能删除。管理器会先创建并验证可恢复的保护快照，再删除此停放存档。继续吗？")) return;
        IsBusy = true;
        DeleteWorldCommand.NotifyCanExecuteChanged();
        try
        {
            var snapshot = await deletion.DeleteAsync(selected.Id);
            await ReloadAsync();
            Feedback = $"存档 {selected.Id} 已删除，保护快照保留：{snapshot}";
        }
        catch (Exception exception) { Feedback = $"删除失败：{exception.Message}"; }
        finally
        {
            IsBusy = false;
            DeleteWorldCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSelected))]
    private async Task EditSelectedWorldSettingsAsync()
    {
        if (SelectedSave is null || _registry is null || EditWorldSettingsAsync is null) return;
        IsBusy = true;
        try
        {
            var slot = slots.GetSlot(_registry, SelectedSave.Id);
            var session = await worldSettings.LoadEditorSessionAsync(slot);
            var result = await EditWorldSettingsAsync(session);
            if (result is null) return;
            await worldSettings.SaveEditorResultAsync(slot, result);
            Feedback = "世界设置已保存，将在此存档下次启动时生效。";
        }
        catch (Exception exception) { Feedback = $"世界设置操作失败：{exception.Message}"; }
        finally
        {
            IsBusy = false;
            EditSelectedWorldSettingsCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedWorldSettingsSourceChanged(WorldSettingsSourceOption value)
    {
        if (value.Mode != NewWorldSettingsMode.Custom)
        {
            _customDraft = null;
            _customGlobals = null;
        }
        EditNewWorldSettingsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task EditNewWorldSettingsAsync()
    {
        if (_registry is null || EditWorldSettingsAsync is null) return;
        try
        {
            var active = slots.GetSlot(_registry, _registry.ActiveSlotId);
            var session = await worldSettings.CreateDraftSessionAsync(SelectedWorldSettingsSource.Mode, active, _customDraft, _customGlobals);
            var result = await EditWorldSettingsAsync(session);
            if (result is null) return;
            _customDraft = result.ProfileValues;
            _customGlobals = result.GlobalValues;
            SelectedWorldSettingsSource = WorldSettingsSources.Single(item => item.Mode == NewWorldSettingsMode.Custom);
            Feedback = "新存档的自定义设置已准备好。";
        }
        catch (Exception exception) { Feedback = $"无法编辑新存档设置：{exception.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanRenameTag))]
    private async Task RenameTagAsync()
    {
        if (SelectedSave is null) return;
        var selectedId = SelectedSave.Id;
        IsBusy = true;
        try
        {
            await slots.RenameTagAsync(selectedId, EditTag);
            await ReloadAsync(selectedId);
            if (_registry is not null) await worldSettings.UpdateProfileTagAsync(slots.GetSlot(_registry, selectedId));
            EditTag = "";
            Feedback = "标签已更新";
        }
        catch (Exception exception) { Feedback = $"标签修改失败：{exception.Message}"; }
        finally
        {
            IsBusy = false;
            RenameTagCommand.NotifyCanExecuteChanged();
            StartSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSelected))]
    private void StartSelected()
    {
        if (SelectedSave is not null) Completed?.Invoke(this, new StartupChoice(StartupMode.Existing, SelectedSave.Id));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var tag = NewTag.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            Feedback = "请输入新存档标签。";
            return;
        }
        try { SaveSlotService.ValidateTag(tag); }
        catch (Exception exception) { Feedback = exception.Message; return; }
        if (SelectedWorldSettingsSource.Mode == NewWorldSettingsMode.Custom && _customDraft is null)
        {
            await EditNewWorldSettingsAsync();
            if (_customDraft is null) return;
        }
        if (ConfirmAsync is null || !await ConfirmAsync("创建新存档", $"将创建一个全新存档，管理标签为【{tag}】。\n世界设置来源：{SelectedWorldSettingsSource.Label}\n\n标签不会修改 ServerName 或游戏中的世界名称。继续吗？")) return;
        var settings = new NewWorldSettingsChoice(
            SelectedWorldSettingsSource.Mode,
            _customDraft is null ? null : new Dictionary<string, string>(_customDraft, StringComparer.OrdinalIgnoreCase),
            _customGlobals is null ? null : new Dictionary<string, string>(_customGlobals, StringComparer.OrdinalIgnoreCase));
        NewTag = "";
        _customDraft = null;
        _customGlobals = null;
        Completed?.Invoke(this, new StartupChoice(StartupMode.Create, Tag: tag, WorldSettings: settings));
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (_registry is null || ChooseBackupAsync is null) return;
        try
        {
            var available = await backups.ListAsync(_registry);
            var choice = await ChooseBackupAsync(available);
            if (choice is not null) Completed?.Invoke(this, new StartupChoice(StartupMode.Rollback, _registry.ActiveSlotId, Backup: choice));
        }
        catch (Exception exception) { Feedback = $"读取备份失败：{exception.Message}"; }
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(this, null);

    private async Task ReloadAsync(int? selectId = null)
    {
        _registry = await slots.LoadAsync();
        var displays = await slots.GetDisplaysAsync(_registry);
        var targetId = selectId ?? _registry.ActiveSlotId;
        Saves.Clear();
        foreach (var display in displays) Saves.Add(display);
        SelectedSave = Saves.FirstOrDefault(save => save.Id == targetId) ?? Saves.FirstOrDefault();
    }

    public Task RefreshSavesAsync(int? selectId = null) => ReloadAsync(selectId);
}
