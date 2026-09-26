using System.Text.Json.Serialization;

namespace HaoHaoTianTian.PalHR.Models;

public sealed class RootCache
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("serverRoot")] public string ServerRoot { get; set; } = "";
    [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
}

public sealed class ManagerSettings
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("idleShutdownEnabled")] public bool IdleShutdownEnabled { get; set; } = true;
    [JsonPropertyName("idleShutdownMinutes")] public int IdleShutdownMinutes { get; set; } = 15;
    [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
}

public sealed class SaveSlotRegistry
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("activeSlotId")] public int ActiveSlotId { get; set; }
    [JsonPropertyName("nextSlotId")] public int NextSlotId { get; set; } = 2;
    [JsonPropertyName("slots")] public List<SaveSlot> Slots { get; set; } = [];
}

public sealed class SaveSlot
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("parkedFolder")] public string ParkedFolder { get; set; } = "";
    [JsonPropertyName("worldGuid")] public string WorldGuid { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }
}
