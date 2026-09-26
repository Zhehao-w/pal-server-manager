using System.Text.Json.Serialization;

namespace HaoHaoTianTian.PalHR.Models;

public sealed record WorldIdentityIssue(string Code, string Message, bool IsHardBlock = true);

public sealed class WorldIdentityAuditResult
{
    public List<WorldIdentityIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(issue => !issue.IsHardBlock);
    public void ThrowIfBlocked()
    {
        var blocked = Issues.Where(issue => issue.IsHardBlock).ToArray();
        if (blocked.Length > 0) throw new InvalidOperationException("世界身份审计未通过：\n\n" + string.Join("\n", blocked.Select(issue => "• " + issue.Message)));
    }
}

public sealed record WorldOptionConflict(string WorldUid, string FilePath, long Length, DateTime LastWriteTimeUtc);
public enum WorldOptionDecision { Cancel, Continue, BackupAndDisable }
public enum UpdateProtectionDecision { Cancel, StartOnly, SnapshotAndStart }

public sealed class PendingOperation
{
    public int SchemaVersion { get; set; } = 1;
    public string Type { get; set; } = "";
    public int FromSlot { get; set; }
    public string FromWorldUid { get; set; } = "";
    public int ToSlot { get; set; }
    public string ToWorldUid { get; set; } = "";
    public string Phase { get; set; } = "Prepared";
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? RecoverySnapshotPath { get; set; }
    public string? SourcePath { get; set; }
    public string? QuarantinePath { get; set; }
}

public sealed class BuildStateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, WorldBuildState> Worlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorldBuildState
{
    public int SlotId { get; set; }
    public string WorldUid { get; set; } = "";
    public string LastSuccessfulBuild { get; set; } = "";
    public DateTimeOffset VerifiedUtc { get; set; }
}

public sealed class SnapshotManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; }
    public string SnapshotType { get; set; } = "";
    public string ManagerVersion { get; set; } = "";
    public string PalServerBuildId { get; set; } = "";
    public int SlotId { get; set; }
    public string WorldUid { get; set; } = "";
    public string ManagementTag { get; set; } = "";
    public WorldSettingsProfile? WorldSettingsProfile { get; set; }
    public string WorldSettingsProfileHash { get; set; } = "";
    public int PlayerSaveCount { get; set; }
    // Optional so previously created protection snapshots remain readable.
    public Dictionary<string, string>? PayloadSha256 { get; set; }
    public long? PayloadBytes { get; set; }
}

// A delete snapshot includes the *entire* parked slot, not just the game
// payload used by rollback. This is the authority for recovering a deleted
// world and its Manager metadata after an interrupted transaction.
public sealed class DeleteWorldSnapshotManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int SlotId { get; set; }
    public string Label { get; set; } = "";
    public string WorldUid { get; set; } = "";
    public string ParkedFolder { get; set; } = "";
    public Dictionary<string, string> FilesSha256 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
