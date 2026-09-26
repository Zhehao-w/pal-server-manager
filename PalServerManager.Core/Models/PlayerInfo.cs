namespace HaoHaoTianTian.PalHR.Models;

public sealed record PlayerInfo(
    string Status,
    string Name,
    string PlayerMeta,
    string Level,
    string Ping,
    string OnlineDuration,
    string LastOnline,
    string Account,
    string PlayerId);

public sealed record PlayerRosterSnapshot(
    IReadOnlyList<PlayerInfo> Players,
    int OnlineCount,
    int SavedCount,
    bool PresenceKnown);

public enum PlayerPresenceMode
{
    OnlineSnapshot,
    ServerStopped,
    Unknown
}
