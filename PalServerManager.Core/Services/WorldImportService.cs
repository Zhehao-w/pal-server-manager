using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public enum WorldImportCheckpoint { BeforeProfileWrite, BeforeRegistryCommit }

/// <summary>Registers an existing parked world in place. SaveGames is never written here.</summary>
public sealed class WorldImportService(
    PalContext context, ServerProcessService processes, SaveSlotService slots,
    WorldSettingsService settings, WorldIdentityAuditService identity,
    WorldDiscoveryService discovery, OperationJournalService journal, LoggingService log,
    Action<WorldImportCheckpoint>? checkpoint = null, Func<bool>? serverRunning = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool IsRunning() => serverRunning?.Invoke() ?? processes.GetSnapshot().IsRunning;

    public async Task<SaveSlot> ImportAsync(string folderName, string expectedUid, string tag,
        ExistingWorldSettingsChoice source, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有导入操作在进行。 ");
        try
        {
            if (IsRunning()) throw new InvalidOperationException("导入前必须关闭 PalServer。 ");
            if (File.Exists(context.StatePaths.PendingOperationPath)) throw new InvalidOperationException("存在未完成的存档事务，不能导入。 ");
            PathSafety.RequireChildName(folderName);
            var expectedPath = PathSafety.RequireInside(context.ServerPaths.SaveRoot,
                Path.Combine(context.ServerPaths.SaveRoot, folderName));
            var registry = await slots.LoadAsync(cancellationToken);
            await identity.AuditOrThrowAsync(registry, cancellationToken: cancellationToken);
            var candidate = await RequireCandidateAsync(registry, folderName, expectedUid, expectedPath, cancellationToken);
            if (registry.NextSlotId < 0 || registry.NextSlotId <= registry.Slots.Max(slot => slot.Id) ||
                registry.NextSlotId == int.MaxValue)
                throw new InvalidOperationException("NextSlotId 无效，不能安全分配新编号。 ");
            var normalizedTag = SaveSlotService.ValidateTag(tag);
            var values = await settings.ResolveExistingWorldSourceAsync(source, registry, cancellationToken);
            var slot = new SaveSlot { Id = registry.NextSlotId, Tag = normalizedTag,
                WorldGuid = candidate.WorldUid!, ParkedFolder = folderName,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            var targetProfile = settings.GetProfilePath(slot.Id);
            if (File.Exists(targetProfile) || File.Exists(targetProfile + ".previous"))
                throw new InvalidOperationException("目标编号已有设置档或历史文件；拒绝覆盖。 ");
            var oldRegistry = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath, cancellationToken);
            var desired = new SaveSlotRegistry { Version = registry.Version, ActiveSlotId = registry.ActiveSlotId,
                NextSlotId = checked(slot.Id + 1), Slots = registry.Slots.Concat([slot]).ToList() };
            var operation = new PendingOperation { Type = "ImportWorld", FromSlot = registry.ActiveSlotId,
                FromWorldUid = registry.Slots.Single(item => item.Id == registry.ActiveSlotId).WorldGuid,
                ToSlot = slot.Id, ToWorldUid = slot.WorldGuid, SourcePath = expectedPath };
            await journal.WriteAsync(operation, "Prepared", cancellationToken);
            try
            {
                checkpoint?.Invoke(WorldImportCheckpoint.BeforeProfileWrite);
                await settings.CommitNewWorldProfileAsync(slot.Id, slot.WorldGuid, slot.Tag, values, cancellationToken);
                await settings.LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
                await journal.WriteAsync(operation, "ProfilePrepared", cancellationToken);
                var beforeCommit = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath, cancellationToken);
                if (IsRunning() || !oldRegistry.SequenceEqual(beforeCommit))
                    throw new InvalidOperationException("导入期间服务器或存档登记发生变化；已取消。 ");
                await RequireCandidateAsync(registry, folderName, expectedUid, expectedPath, cancellationToken);
                await identity.AuditOrThrowAsync(registry, checkInterruptedOperation: false, cancellationToken: cancellationToken);
                checkpoint?.Invoke(WorldImportCheckpoint.BeforeRegistryCommit);
                await slots.SaveAsync(desired, cancellationToken); // Registry is the final authority commit.
                journal.Complete();
                await log.WriteAsync($"Imported existing save {slot.Id} ({slot.Tag}), UID {slot.WorldGuid}, folder {slot.ParkedFolder}.", CancellationToken.None);
                return slot;
            }
            catch
            {
                // If the registry commit actually completed, the profile is valid and
                // preserving the committed pair is safer than deleting one half.
                try
                {
                    var current = await slots.LoadAsync();
                    if (current.Slots.Any(item => item.Id == slot.Id && item.WorldGuid.Equals(slot.WorldGuid, StringComparison.OrdinalIgnoreCase) &&
                        item.ParkedFolder.Equals(slot.ParkedFolder, StringComparison.OrdinalIgnoreCase)) &&
                        current.NextSlotId == desired.NextSlotId)
                    {
                        await settings.LoadProfileAsync(slot.Id, slot.WorldGuid);
                        journal.Complete();
                        return slot;
                    }
                    var afterFailure = await File.ReadAllBytesAsync(context.StatePaths.RegistryPath);
                    if (oldRegistry.SequenceEqual(afterFailure))
                    {
                        settings.DeleteProfileIfExists(slot.Id);
                        journal.Complete();
                    }
                    // Ambiguous external changes retain PendingOperation and profile.
                }
                catch { /* Keep transaction authority for inspection. */ }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<WorldObservation> RequireCandidateAsync(SaveSlotRegistry registry, string folderName,
        string expectedUid, string expectedPath, CancellationToken cancellationToken)
    {
        var report = await discovery.ScanAsync(registry, cancellationToken);
        var candidate = report.Worlds.SingleOrDefault(world => world.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (candidate is null || !candidate.CanImport || !candidate.FolderPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.WorldUid, expectedUid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("候选存档已变化、损坏或 UID 重复；请重新扫描。 ");
        return candidate;
    }
}
