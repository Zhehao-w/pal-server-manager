namespace HaoHaoTianTian.PalHR.Models;

public sealed record SaveSlotDisplay(
    int Id,
    string Tag,
    string State,
    string WorldUid,
    string Size,
    string Players,
    string Updated);

public enum BackupKind
{
    Native,
    Protection,
    PreUpdate
}

public sealed record BackupEntry(
    BackupKind Kind,
    string KindLabel,
    string Name,
    DateTimeOffset CapturedAt,
    string TimeText,
    int PlayerCount,
    double SizeMb,
    string Path)
{
    public string DisplayText => $"{TimeText} · {KindLabel} · {PlayerCount} 位玩家 · {SizeMb:N2} MB";
    public bool HasManagerManifest { get; init; }
    public bool RestoreWorldSettings { get; init; }
}

public enum StartupMode
{
    Existing,
    Create,
    Rollback
}

public sealed record StartupChoice(
    StartupMode Mode,
    int? SlotId = null,
    string? Tag = null,
    BackupEntry? Backup = null,
    NewWorldSettingsChoice? WorldSettings = null);
