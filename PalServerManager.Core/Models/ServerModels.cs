namespace HaoHaoTianTian.PalHR.Models;

public sealed record ServerProcessSnapshot(
    bool IsRunning,
    IReadOnlyList<int> ProcessIds,
    double TotalCpuSeconds,
    double MemoryMb,
    DateTimeOffset? StartedAt);

public sealed record RestConfiguration(string Password, int Port);

public sealed record ServerInfo(string ServerName, string Version, string WorldGuid);

public sealed record ServerMetrics(double? ServerFps, double? ServerFrameTime, int? CurrentPlayers, int? MaxPlayers, double? UptimeSeconds);

public sealed record RestPlayer(string Name, int Level, double? Ping, string PlayerId, string UserId, string AccountName);

public sealed record StatusSnapshot(
    ServerProcessSnapshot Process,
    ServerInfo? Info,
    ServerMetrics? Metrics,
    IReadOnlyList<RestPlayer>? Players,
    double? FreeDiskGb,
    bool InfoAttempted,
    bool InfoSucceeded,
    bool MetricsAttempted,
    bool MetricsSucceeded,
    bool PlayersAttempted,
    bool PlayersSucceeded);
