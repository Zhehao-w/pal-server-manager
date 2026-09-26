using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed partial class WorldIdentityAuditService(
    PalContext context,
    SaveSlotService slots,
    WorldSettingsService worldSettings)
{
    public async Task<WorldIdentityAuditResult> AuditAsync(SaveSlotRegistry registry, bool requireRuntimeIdentity = true, bool checkInterruptedOperation = true, CancellationToken cancellationToken = default)
    {
        var result = new WorldIdentityAuditResult();
        var duplicateIds = registry.Slots.GroupBy(slot => slot.Id).Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateIds)
            result.Issues.Add(new("DuplicateSlot", $"存档编号 {duplicate.Key} 被登记了多次。"));

        foreach (var group in registry.Slots.Where(slot => WorldUidRegex().IsMatch(slot.WorldGuid ?? ""))
                     .GroupBy(slot => slot.WorldGuid.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var owners = string.Join("；", group.Select(slot => $"{slot.Id} · {slot.Tag}"));
            result.Issues.Add(new("DuplicateWorldUid",
                $"检测到重复世界 UID {group.Key}：{owners}。两个世界共用同一个客户端 LocalData 文件夹，会混合地图探索、地图界面状态、本地提示及其他客户端世界状态。"));
        }

        foreach (var slot in registry.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WorldUidRegex().IsMatch(slot.WorldGuid ?? ""))
            {
                result.Issues.Add(new("InvalidWorldUid", $"存档 {slot.Id} · {slot.Tag} 的世界 UID 无效或为空：{slot.WorldGuid}"));
                continue;
            }
            try { PathSafety.RequireChildName(slot.ParkedFolder); }
            catch (Exception exception) { result.Issues.Add(new("InvalidParkedPath", $"存档 {slot.Id} 的停放目录无效：{exception.Message}")); }

            var path = slot.Id == registry.ActiveSlotId ? context.ServerPaths.ActivePath : Path.Combine(context.ServerPaths.SaveRoot, slot.ParkedFolder);
            if (!Directory.Exists(path))
            {
                result.Issues.Add(new("MissingSlotDirectory", $"存档 {slot.Id} · {slot.Tag} 的目录不存在：{path}"));
                continue;
            }
            var actualWorlds = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(name => name is not null && WorldUidRegex().IsMatch(name)).Cast<string>().ToArray();
            if (!actualWorlds.Contains(slot.WorldGuid, StringComparer.OrdinalIgnoreCase))
                result.Issues.Add(new("DirectoryUidMismatch", $"存档 {slot.Id} · {slot.Tag} 的目录不包含登记的世界 UID {slot.WorldGuid}；实际检测到：{string.Join(", ", actualWorlds.DefaultIfEmpty("无"))}。"));
            if (actualWorlds.Length != 1)
                result.Issues.Add(new("AmbiguousWorldDirectory", $"存档 {slot.Id} · {slot.Tag} 应只包含一个世界 UID 目录，实际为 {actualWorlds.Length} 个。"));
            try { await worldSettings.LoadProfileAsync(slot.Id, slot.WorldGuid!, cancellationToken); }
            catch (Exception exception) { result.Issues.Add(new("ProfileIdentity", exception.Message)); }
        }

        foreach (var group in registry.Slots.GroupBy(slot => slot.ParkedFolder, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            result.Issues.Add(new("DuplicateParkedMapping", $"停放目录 {group.Key} 被多个存档登记：{string.Join(", ", group.Select(slot => slot.Id))}。"));

        var diskUids = registry.Slots.SelectMany(slot =>
        {
            var path = slot.Id == registry.ActiveSlotId ? context.ServerPaths.ActivePath : Path.Combine(context.ServerPaths.SaveRoot, slot.ParkedFolder);
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).Where(name => name is not null && WorldUidRegex().IsMatch(name)).Cast<string>().Select(uid => (slot, uid))
                : [];
        });
        foreach (var group in diskUids.GroupBy(item => item.uid, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(item => item.slot.Id).Distinct().Count() > 1))
            result.Issues.Add(new("DuplicateDiskUid", $"磁盘上多个存档目录包含同一世界 UID {group.Key}：{string.Join("；", group.Select(item => $"{item.slot.Id} · {item.slot.Tag}"))}。"));

        if (requireRuntimeIdentity && registry.Slots.FirstOrDefault(slot => slot.Id == registry.ActiveSlotId) is { } active)
        {
            try
            {
                var configured = await slots.GetWorldGuidAsync(cancellationToken);
                if (!string.Equals(configured, active.WorldGuid, StringComparison.OrdinalIgnoreCase))
                    result.Issues.Add(new("DedicatedServerNameMismatch", $"GameUserSettings.ini 的 DedicatedServerName 为 {configured}，但当前存档 {active.Id} · {active.Tag} 应为 {active.WorldGuid}。"));
            }
            catch (Exception exception) { result.Issues.Add(new("DedicatedServerNameInvalid", exception.Message)); }
        }

        var artifacts = Directory.Exists(context.ServerPaths.SaveRoot)
            ? Directory.EnumerateFileSystemEntries(context.ServerPaths.SaveRoot, ".pal-*", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        if (checkInterruptedOperation && (File.Exists(context.StatePaths.PendingOperationPath) || artifacts.Length > 0))
            result.Issues.Add(new("InterruptedOperation", $"检测到未完成的存档事务。事务记录：{(File.Exists(context.StatePaths.PendingOperationPath) ? context.StatePaths.PendingOperationPath : "无")}；残留目录：{string.Join(", ", artifacts.Select(Path.GetFileName).DefaultIfEmpty("无"))}。为避免误判 active 存档，已禁止启动。"));
        return result;
    }

    public async Task AuditOrThrowAsync(SaveSlotRegistry registry, bool requireRuntimeIdentity = true, bool checkInterruptedOperation = true, CancellationToken cancellationToken = default) =>
        (await AuditAsync(registry, requireRuntimeIdentity, checkInterruptedOperation, cancellationToken)).ThrowIfBlocked();

    [GeneratedRegex("^[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex WorldUidRegex();
}
