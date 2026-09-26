using System.Security.Cryptography;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public enum WorldImportCheckpoint { BeforeProfileWrite, BeforeRegistryCommit }

/// <summary>Copies an explicitly selected world to a verified canonical parked slot.</summary>
public sealed class WorldImportService(
    PalContext context, ServerProcessService processes, SaveSlotService slots,
    WorldSettingsService settings, WorldIdentityAuditService identity,
    WorldDiscoveryService discovery, WorldOptionService worldOptions,
    OperationJournalService journal, LoggingService log,
    Action<WorldImportCheckpoint>? checkpoint = null, Func<bool>? serverRunning = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool IsRunning() => serverRunning?.Invoke() ?? processes.GetSnapshot().IsRunning;

    public async Task<WorldImportResult> ImportAsync(string sourcePath, string expectedUid, string tag,
        ExistingWorldSettingsChoice sourceSettings, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有导入操作在进行。");
        try
        {
            if (IsRunning()) throw new InvalidOperationException("导入前必须关闭 PalServer。");
            if (File.Exists(context.StatePaths.PendingOperationPath)) throw new InvalidOperationException("存在未完成的存档事务，不能导入。");
            var selectedPath = Path.GetFullPath(sourcePath);
            var registry = await slots.LoadAsync(cancellationToken);
            await identity.AuditOrThrowAsync(registry, cancellationToken: cancellationToken);
            var candidate = await RequireCandidateAsync(registry, selectedPath, expectedUid, cancellationToken);
            if (registry.NextSlotId < 0 || registry.NextSlotId <= registry.Slots.Max(slot => slot.Id) ||
                registry.NextSlotId == int.MaxValue)
                throw new InvalidOperationException("NextSlotId 无效，不能安全分配新编号。");

            var normalizedTag = SaveSlotService.ValidateTag(tag);
            var folderName = SaveSlotService.NewParkedFolderName(registry.NextSlotId, normalizedTag);
            var destination = PathSafety.RequireInside(context.ServerPaths.SaveRoot,
                Path.Combine(context.ServerPaths.SaveRoot, folderName));
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException($"规范停放目录已存在，不能覆盖：{destination}");
            var values = await settings.ResolveExistingWorldSourceAsync(sourceSettings, registry, cancellationToken);
            var slot = new SaveSlot { Id = registry.NextSlotId, Tag = normalizedTag,
                WorldGuid = candidate.WorldUid!, ParkedFolder = folderName,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            var profilePath = settings.GetProfilePath(slot.Id);
            if (File.Exists(profilePath) || File.Exists(profilePath + ".previous"))
                throw new InvalidOperationException("目标编号已有设置档或历史文件；拒绝覆盖。");

            var worldSource = candidate.WorldPath!;
            BackupService.RejectReparsePoints(worldSource); // Deep checks belong to copy, not Rescan.
            var sourceHashes = HashFiles(worldSource);
            var oldRegistry = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath, cancellationToken);
            var desired = new SaveSlotRegistry { Version = registry.Version, ActiveSlotId = registry.ActiveSlotId,
                NextSlotId = checked(slot.Id + 1), Slots = registry.Slots.Concat([slot]).ToList() };
            var staging = PathSafety.RequireInside(context.ServerPaths.SaveRoot,
                Path.Combine(context.ServerPaths.SaveRoot, $".pal-import-{Guid.NewGuid():N}"));
            var publishedWorld = Path.Combine(destination, slot.WorldGuid);
            var operation = new PendingOperation { Type = "ImportWorld", FromSlot = registry.ActiveSlotId,
                FromWorldUid = registry.Slots.Single(item => item.Id == registry.ActiveSlotId).WorldGuid,
                ToSlot = slot.Id, ToWorldUid = slot.WorldGuid, SourcePath = worldSource, QuarantinePath = destination };
            var journalCreated = false;
            Dictionary<string, string>? publishedHashes = null;
            try
            {
                Directory.CreateDirectory(staging);
                CopyWorld(worldSource, Path.Combine(staging, slot.WorldGuid));
                if (!SameHashes(sourceHashes, HashFiles(worldSource)) ||
                    !SameHashes(sourceHashes, HashFiles(Path.Combine(staging, slot.WorldGuid))))
                    throw new InvalidOperationException("复制后的文件校验失败，未登记导入存档。");
                if (IsRunning()) throw new InvalidOperationException("复制期间 PalServer 已启动，已取消导入。");
                await RequireCandidateAsync(registry, selectedPath, expectedUid, cancellationToken);

                await journal.WriteAsync(operation, "CopiedAndVerified", cancellationToken);
                journalCreated = true;
                Directory.Move(staging, destination); // Publish on the destination SaveGames volume.
                await journal.WriteAsync(operation, "Published", cancellationToken);
                foreach (var conflict in worldOptions.Detect(desired, slot.Id))
                    await worldOptions.BackupAndDisableAsync(conflict, cancellationToken);
                publishedHashes = HashFiles(publishedWorld);
                checkpoint?.Invoke(WorldImportCheckpoint.BeforeProfileWrite);
                await settings.CommitNewWorldProfileAsync(slot.Id, slot.WorldGuid, slot.Tag, values, cancellationToken);
                await settings.LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
                await journal.WriteAsync(operation, "ProfilePrepared", cancellationToken);

                var beforeCommit = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath, cancellationToken);
                if (IsRunning() || !oldRegistry.SequenceEqual(beforeCommit) ||
                    !SameHashes(sourceHashes, HashFiles(worldSource)) ||
                    !SameHashes(publishedHashes, HashFiles(publishedWorld)))
                    throw new InvalidOperationException("导入期间服务器、来源或登记内容发生变化；已取消。");
                await identity.AuditOrThrowAsync(registry, checkInterruptedOperation: false, cancellationToken: cancellationToken);
                checkpoint?.Invoke(WorldImportCheckpoint.BeforeRegistryCommit);
                await slots.SaveAsync(desired, cancellationToken); // Registry is the final authority commit.
                journal.Complete();
            }
            catch (Exception original)
            {
                var recoveryErrors = new List<Exception>();
                try
                {
                    var current = await slots.LoadAsync();
                    if (current.Slots.Any(item => item.Id == slot.Id &&
                            item.WorldGuid.Equals(slot.WorldGuid, StringComparison.OrdinalIgnoreCase) &&
                            item.ParkedFolder.Equals(slot.ParkedFolder, StringComparison.OrdinalIgnoreCase)) &&
                        current.NextSlotId == desired.NextSlotId)
                    {
                        await settings.LoadProfileAsync(slot.Id, slot.WorldGuid);
                        journal.Complete();
                        return await FinishCommittedAsync(slot, candidate, sourceHashes);
                    }
                    var afterFailure = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath);
                    if (!oldRegistry.SequenceEqual(afterFailure))
                        throw new InvalidOperationException("登记内容已变化，保留事务证据供检查。");
                    if (Directory.Exists(destination))
                    {
                        if (publishedHashes is null || !SameHashes(publishedHashes, HashFiles(publishedWorld)))
                            throw new InvalidOperationException("已发布目录状态不明，保留事务证据供检查。");
                        BackupService.RejectReparsePoints(destination);
                        Directory.Delete(destination, recursive: true);
                    }
                    if (Directory.Exists(staging))
                    {
                        BackupService.RejectReparsePoints(staging);
                        Directory.Delete(staging, recursive: true);
                    }
                    settings.DeleteProfileIfExists(slot.Id);
                    if (journalCreated) journal.Complete();
                }
                catch (Exception recoveryError) { recoveryErrors.Add(recoveryError); }
                if (recoveryErrors.Count > 0)
                    throw new AggregateException("导入失败且无法验证自动回滚；保留事务证据供检查。", new[] { original }.Concat(recoveryErrors));
                throw;
            }

            return await FinishCommittedAsync(slot, candidate, sourceHashes);
        }
        finally { _gate.Release(); }
    }

    private async Task<WorldImportResult> FinishCommittedAsync(SaveSlot slot, WorldObservation candidate,
        Dictionary<string, string> sourceHashes)
    {
        string? warning = null;
        try { warning = CleanupCurrentSaveGamesSource(candidate, sourceHashes); }
        catch (Exception error) { warning = $"已导入存档，但旧来源未能移除：{error.Message} 请检查重复 UID 文件夹。"; }
        try { await log.WriteAsync($"Imported save {slot.Id} ({slot.Tag}), UID {slot.WorldGuid}, canonical folder {slot.ParkedFolder}. {warning}", CancellationToken.None); }
        catch (Exception error) { warning = $"{warning} 导入已完成，但日志写入失败：{error.Message}".Trim(); }
        return new(slot, warning);
    }

    private string? CleanupCurrentSaveGamesSource(WorldObservation candidate, Dictionary<string, string> sourceHashes)
    {
        var saveRoot = Path.GetFullPath(context.ServerPaths.SaveRoot).TrimEnd(Path.DirectorySeparatorChar);
        var source = Path.GetFullPath(candidate.FolderPath).TrimEnd(Path.DirectorySeparatorChar);
        string? container = null;
        if (string.Equals(Path.GetDirectoryName(source), saveRoot, StringComparison.OrdinalIgnoreCase))
            container = source;
        else if (string.Equals(source, candidate.WorldPath, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(source)!;
            if (string.Equals(Path.GetDirectoryName(parent), saveRoot, StringComparison.OrdinalIgnoreCase))
                container = parent;
        }
        if (container is null) return null; // External sources are never removed.
        PathSafety.RequireInside(saveRoot, container);
        PathSafety.RequireChildName(Path.GetFileName(container));
        if (!Directory.Exists(container)) return "已导入存档，但原 SaveGames 来源已消失；请检查来源目录。";
        BackupService.RejectReparsePoints(container);
        if ((!string.Equals(container, candidate.WorldPath, StringComparison.OrdinalIgnoreCase) &&
             Directory.EnumerateFileSystemEntries(container, "*", SearchOption.TopDirectoryOnly)
                 .Any(path => !string.Equals(path, candidate.WorldPath, StringComparison.OrdinalIgnoreCase))) ||
            !SameHashes(sourceHashes, HashFiles(candidate.WorldPath!)))
            return "已导入存档，但旧来源含额外内容或已变化，未自动删除；请检查重复 UID 文件夹。";
        Directory.Delete(container, recursive: true);
        return null;
    }

    private async Task<WorldObservation> RequireCandidateAsync(SaveSlotRegistry registry, string selectedPath,
        string expectedUid, CancellationToken cancellationToken)
    {
        var report = await discovery.ScanSourceAsync(selectedPath, registry, cancellationToken);
        var candidate = report.Worlds.SingleOrDefault(world =>
            string.Equals(world.FolderPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (candidate is null || !candidate.CanImport || candidate.WorldPath is null ||
            !string.Equals(candidate.WorldUid, expectedUid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("候选存档已变化、损坏或 UID 重复；请重新扫描。");
        return candidate;
    }

    private static void CopyWorld(string source, string destination)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current.Source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("来源世界包含重解析点，不能安全复制。");
            Directory.CreateDirectory(current.Destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(current.Source, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("来源世界包含重解析点，不能安全复制。");
                var target = Path.Combine(current.Destination, Path.GetFileName(child));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((child, target));
                else File.Copy(child, target, overwrite: false);
            }
        }
    }

    private static Dictionary<string, string> HashFiles(string root)
    {
        BackupService.RejectReparsePoints(root);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(root, path),
            path => HashFile(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SameHashes(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count && expected.All(item => actual.TryGetValue(item.Key, out var hash) &&
            string.Equals(item.Value, hash, StringComparison.OrdinalIgnoreCase));
}
