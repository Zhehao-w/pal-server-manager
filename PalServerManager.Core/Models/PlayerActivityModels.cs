using System.Text.Json.Serialization;

namespace HaoHaoTianTian.PalHR.Models;

public sealed class PlayerActivityDocument
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("worlds")] public Dictionary<string, PlayerWorldActivity> Worlds { get; set; } = [];
}

public sealed class PlayerWorldActivity
{
    [JsonPropertyName("players")] public Dictionary<string, PlayerActivityRecord> Players { get; set; } = [];
}

public sealed class PlayerActivityRecord
{
    [JsonPropertyName("playerId")] public string PlayerId { get; set; } = "";
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("accountName")] public string AccountName { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("firstSeenUtc")] public DateTimeOffset? FirstSeenUtc { get; set; }
    [JsonPropertyName("lastSeenUtc")] public DateTimeOffset? LastSeenUtc { get; set; }
    [JsonPropertyName("lastLeftUtc")] public DateTimeOffset? LastLeftUtc { get; set; }
    [JsonPropertyName("lastKnownLevel")] public int? LastKnownLevel { get; set; }
}
