using System.Text.Json;
using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

/// <summary>Top-level SaveGames observation only. This service never writes game or Manager state.</summary>
public sealed partial class WorldDiscoveryService(ServerPaths server, ServerStatePaths? state = null)
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    { "backup", "backups", "savebackups", "snapshots", "evidence", "staging" };

    public async Task<WorldDiscoveryReport> ScanAsync(SaveSlotRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        var worlds = new List<WorldObservation>();
        var root = server.SaveRoot;
        if (Directory.Exists(root))
        {
            if (HasReparsePoint(root))
                throw new InvalidOperationException("SaveGames 根目录是重解析点，不能安全扫描世界。");
            foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var slot = registry?.Slots.FirstOrDefault(item =>
                    item.Id == registry.ActiveSlotId ? name.Equals("0", StringComparison.OrdinalIgnoreCase) :
                    name.Equals(item.ParkedFolder, StringComparison.OrdinalIgnoreCase));
                if (slot is null && IsInternalOrBackup(name)) continue;
                worlds.Add(InspectFolder(root, path, name, slot, registry));
            }
        }

        if (registry is not null)
        {
            foreach (var slot in registry.Slots)
            {
                var name = slot.Id == registry.ActiveSlotId ? "0" : slot.ParkedFolder;
                if (worlds.Any(world => world.SlotId == slot.Id)) continue;
                var path = PathSafety.RequireInside(root, Path.Combine(root, name));
                worlds.Add(new(name, path, null, slot.Id, slot.Tag, WorldDiscoveryStatus.RegisteredMissing,
                    "登记的存档目录不存在。"));
            }
            var registeredUids = registry.Slots.Select(slot => slot.WorldGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diskGroups = worlds.Where(world => world.WorldUid is not null)
                .GroupBy(world => world.WorldUid!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < worlds.Count; index++)
            {
                var world = worlds[index];
                if (world.SlotId is null && world.WorldUid is not null &&
                    (registeredUids.Contains(world.WorldUid) || diskGroups[world.WorldUid] > 1))
                    worlds[index] = world with { Status = WorldDiscoveryStatus.DuplicateUid,
                        Reason = "世界 UID 与已登记或另一未登记存档重复；不能导入。" };
            }
        }
        else
        {
            var duplicates = worlds.Where(world => world.WorldUid is not null)
                .GroupBy(world => world.WorldUid!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < worlds.Count; index++)
                if (worlds[index].WorldUid is { } uid && duplicates.Contains(uid))
                    worlds[index] = worlds[index] with { Status = WorldDiscoveryStatus.DuplicateUid,
                        Reason = "多个磁盘目录含同一世界 UID；无法安全接入。" };
        }

        var profiles = registry is null || state is null ? [] : await InspectProfilesAsync(registry, cancellationToken);
        return new(worlds.OrderBy(world => world.FolderName == "0" ? "" : world.FolderName,
            StringComparer.OrdinalIgnoreCase).ToArray(), profiles);
    }

    private static WorldObservation InspectFolder(string root, string path, string name, SaveSlot? slot, SaveSlotRegistry? registry)
    {
        var unusual = name != "0" && !CanonicalNameRegex().IsMatch(name);
        WorldObservation Result(string? uid, WorldDiscoveryStatus status, string reason, bool hasOption = false) =>
            new(name, path, uid, slot?.Id, slot?.Tag, status, reason, hasOption, unusual);
        try
        {
            PathSafety.RequireInside(root, path);
            if (name != "0") PathSafety.RequireChildName(name);
            if (HasReparsePoint(root) || HasReparsePoint(path))
                return Result(null, slot is null ? WorldDiscoveryStatus.Incomplete : WorldDiscoveryStatus.RegisteredIncomplete,
                    "目录是重解析点，不能确认其位于 SaveGames 内。");
            var uidPaths = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Where(candidate => UidRegex().IsMatch(Path.GetFileName(candidate))).ToArray();
            if (uidPaths.Length != 1)
                return Result(null, slot is null ? WorldDiscoveryStatus.Incomplete : WorldDiscoveryStatus.RegisteredIncomplete,
                    $"应恰有一个 32 位世界 UID 目录，实际为 {uidPaths.Length} 个。");
            var uidPath = PathSafety.RequireInside(root, uidPaths[0]);
            var uid = Path.GetFileName(uidPath).ToUpperInvariant();
            if (HasReparsePoint(uidPath))
                return Result(uid, slot is null ? WorldDiscoveryStatus.Incomplete : WorldDiscoveryStatus.RegisteredIncomplete,
                    "世界 UID 目录是重解析点，不能导入。");
            RejectNestedReparsePoints(uidPath);
            if (slot is not null && !uid.Equals(slot.WorldGuid, StringComparison.OrdinalIgnoreCase))
                return Result(uid, WorldDiscoveryStatus.RegisteredIdentityMismatch,
                    $"磁盘 UID {uid} 与登记 UID {slot.WorldGuid} 不一致；不能自动重认。 ");
            var missing = new[] { "Level.sav", "LevelMeta.sav" }
                .Where(file => !File.Exists(Path.Combine(uidPath, file))).ToArray();
            if (missing.Length > 0)
                return Result(uid, slot is null ? WorldDiscoveryStatus.Incomplete : WorldDiscoveryStatus.RegisteredIncomplete,
                    "缺少 " + string.Join("、", missing) + "。 ");
            var worldOption = File.Exists(Path.Combine(uidPath, "WorldOption.sav")) ||
                              File.Exists(Path.Combine(uidPath, "WorldOptions.sav"));
            if (slot is not null) return Result(uid, WorldDiscoveryStatus.RegisteredHealthy, "登记身份及必要文件一致。", worldOption);
            if (name == "0" && registry is not null)
                return Result(uid, WorldDiscoveryStatus.Ignored, "未登记的 active 目录不能作为附加世界导入。", worldOption);
            return Result(uid, WorldDiscoveryStatus.Importable, "未登记的完整世界；需显式导入。", worldOption);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Result(null, slot is null ? WorldDiscoveryStatus.Incomplete : WorldDiscoveryStatus.RegisteredIncomplete,
                "无法安全检查目录：" + error.Message);
        }
    }

    private async Task<IReadOnlyList<WorldProfileObservation>> InspectProfilesAsync(SaveSlotRegistry registry, CancellationToken ct)
    {
        var results = new List<WorldProfileObservation>();
        foreach (var slot in registry.Slots)
        {
            ct.ThrowIfCancellationRequested();
            var path = state!.WorldProfilePath(slot.Id);
            if (!File.Exists(path)) { results.Add(new(slot.Id, path, WorldProfileStatus.ProfileMissing, "设置档缺失；不会自动创建。")); continue; }
            try
            {
                await using var stream = File.OpenRead(path);
                var profile = await JsonSerializer.DeserializeAsync<WorldSettingsProfile>(stream, cancellationToken: ct)
                    ?? throw new JsonException("设置档为空。");
                if (profile.SlotId != slot.Id || !string.Equals(profile.WorldGuid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase))
                    results.Add(new(slot.Id, path, WorldProfileStatus.ProfileIdentityMismatch, "设置档编号或 UID 与登记不一致；不可猜测归属。"));
                else
                {
                    try
                    {
                        if (profile.SchemaVersion != WorldSettingsService.CurrentProfileSchema || profile.Values is null)
                            throw new InvalidOperationException("设置档版本或字段不完整。");
                        WorldSettingsService.ValidateProfileValues(profile.Values);
                        results.Add(new(slot.Id, path, WorldProfileStatus.ProfileHealthy, "设置档身份一致。"));
                    }
                    catch (InvalidOperationException error)
                    { results.Add(new(slot.Id, path, WorldProfileStatus.ProfileInvalid, error.Message)); }
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { results.Add(new(slot.Id, path, WorldProfileStatus.ProfileInvalid, "设置档无法读取：" + error.Message)); }
        }
        if (Directory.Exists(state!.WorldSettingsRoot))
            foreach (var path in Directory.EnumerateFiles(state.WorldSettingsRoot, "slot-*.json", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (registry.Slots.Any(slot => string.Equals(path, state.WorldProfilePath(slot.Id), StringComparison.OrdinalIgnoreCase))) continue;
                results.Add(new(null, path, WorldProfileStatus.OrphanProfile, "没有登记存档拥有此设置档；不会自动删除或关联。"));
            }
        return results;
    }

    private static bool HasReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void RejectNestedReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("世界目录包含重解析点，不能确认存档文件都位于 SaveGames 内。");
            if ((attributes & FileAttributes.Directory) == 0) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                pending.Push(child);
        }
    }
    private static bool IsInternalOrBackup(string name) => name.StartsWith(".", StringComparison.Ordinal) ||
        IgnoredNames.Contains(name) || name.StartsWith("pal-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("switch-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("restore-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("rollback-", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex UidRegex();
    [GeneratedRegex("^0 - Slot [0-9]{3,} - .+$", RegexOptions.CultureInvariant)] private static partial Regex CanonicalNameRegex();
}
