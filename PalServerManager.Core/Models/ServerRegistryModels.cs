using System.Text.Json.Serialization;

namespace HaoHaoTianTian.PalHR.Models;

public sealed class ServerRegistryDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("selectedServerId")] public string? SelectedServerId { get; set; }
    [JsonPropertyName("servers")] public List<RegisteredServer> Servers { get; set; } = [];
}

public enum ServerStateKind
{
    InvalidServerRoot,
    NeedsInitialization,
    Ready,
    EmptyNoWorld,
    Interrupted,
    InvalidState
}

public sealed record ServerStateAssessment(ServerStateKind Kind, string Message, RegisteredServer? Server = null);

public sealed class StateActivationRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerId { get; set; } = "";
    public string Mode { get; set; } = ""; // Existing stored values are accepted without an old-manager dependency.
    public DateTimeOffset ActivatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
