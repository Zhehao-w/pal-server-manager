namespace HaoHaoTianTian.PalHR.Models;

public enum WorldDiscoveryStatus
{
    RegisteredHealthy,
    RegisteredMissing,
    RegisteredIdentityMismatch,
    RegisteredIncomplete,
    Importable,
    DuplicateUid,
    Incomplete,
    Ignored
}

public sealed record WorldObservation(
    string FolderName,
    string FolderPath,
    string? WorldUid,
    int? SlotId,
    string? Tag,
    WorldDiscoveryStatus Status,
    string Reason,
    bool HasWorldOption = false,
    bool UnusualFolderName = false)
{
    public bool CanImport => Status == WorldDiscoveryStatus.Importable && FolderName != "0";
}

public enum WorldProfileStatus
{
    ProfileHealthy,
    ProfileMissing,
    ProfileInvalid,
    ProfileIdentityMismatch,
    OrphanProfile
}

public sealed record WorldProfileObservation(int? SlotId, string Path, WorldProfileStatus Status, string Reason);

public sealed record WorldDiscoveryReport(
    IReadOnlyList<WorldObservation> Worlds,
    IReadOnlyList<WorldProfileObservation> Profiles)
{
    public int ImportableCount => Worlds.Count(world => world.CanImport);
    public bool HasIssues => Worlds.Any(world => world.Status is not WorldDiscoveryStatus.RegisteredHealthy and not WorldDiscoveryStatus.Ignored) ||
                             Profiles.Any(profile => profile.Status != WorldProfileStatus.ProfileHealthy);
}

public enum ExistingWorldSettingsMode
{
    ActiveProfile,
    OtherProfile,
    CurrentServerIni,
    GameDefaults,
    Custom,
    ExternalIni
}

public sealed record ExistingWorldSettingsChoice(
    ExistingWorldSettingsMode Mode,
    int? SourceSlotId = null,
    Dictionary<string, string>? CustomValues = null,
    string? ExternalIniPath = null);
