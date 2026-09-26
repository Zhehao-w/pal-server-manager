namespace HaoHaoTianTian.PalHR.Models;

public enum WorldSettingKind
{
    Number,
    Integer,
    Boolean,
    Choice,
    Text,
    PlatformList
}

public sealed record WorldSettingDefinition(
    string Key,
    string Category,
    string Label,
    string Description,
    WorldSettingKind Kind,
    double Minimum = 0,
    double Maximum = 20,
    double Step = 0.1,
    IReadOnlyList<string>? Choices = null,
    bool IsGlobal = false,
    bool IsSecret = false);

public sealed class WorldSettingsProfile
{
    public int SchemaVersion { get; set; } = 3;
    public int SlotId { get; set; }
    public string WorldGuid { get; set; } = "";
    public string Tag { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum NewWorldSettingsMode
{
    CopyCurrent,
    GameDefaults,
    Custom
}

public sealed record WorldSettingsSourceOption(NewWorldSettingsMode Mode, string Label, string Description);

public sealed record NewWorldSettingsChoice(
    NewWorldSettingsMode Mode,
    Dictionary<string, string>? CustomValues = null,
    Dictionary<string, string>? GlobalValues = null);

public sealed record WorldSettingsEditorSession(
    int SlotId,
    string Tag,
    string WorldGuid,
    Dictionary<string, string> ProfileValues,
    Dictionary<string, string> GlobalValues,
    bool IsDraft);

public sealed record WorldSettingsEditorResult(
    Dictionary<string, string> ProfileValues,
    Dictionary<string, string> GlobalValues,
    bool RestartRequested);
