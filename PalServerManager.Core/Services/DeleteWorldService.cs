using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public enum DeleteWorldCheckpoint { SnapshotCreated, WorldStaged, MetadataUpdated, BeforePurge }

/// <summary>Deletes only a registered, inactive, parked world after a complete protective snapshot.</summary>
public sealed class DeleteWorldService(
    PalContext context, ServerProcessService processes, SaveSlotService slots,
    WorldIdentityAuditService identity, BackupService backups, SafeFileService files,
    OperationJournalService journal, LoggingService log,
    Action<DeleteWorldCheckpoint>? checkpoint = null,
    Func<bool>? serverRunning = null,
    Action<string>? purge = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool IsServerRunning() => serverRunning?.Invoke() ?? processes.GetSnapshot().IsRunning;

    public async Task<string> DeleteAsync(int slotId, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("另一个删除操作正在进行。");
        try
        {
            // Always reload authoritative state. A selection made earlier is not
            // authority to delete after a switch or external state change.
            if (IsServerRunning()) throw new InvalidOperationException("删除存档前必须关闭服务器。");
            if (File.Exists(context.StatePaths.PendingOperationPath)) throw new InvalidOperationException("存在未完成的存档事务，已拒绝删除。");
            var registry = await slots.LoadAsync(cancellationToken);
            await identity.AuditOrThrowAsync(registry, cancellationToken: cancellationToken);
            if (slotId == registry.ActiveSlotId) throw new InvalidOperationException("不能删除当前存档；请先切换其他世界并关闭服务器。");
            var slot = slots.GetSlot(registry, slotId);
            if (!slot.ParkedFolder.StartsWith($"0 - Slot {slot.Id:D3} - ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("所选世界不是管理器停放的存档，已拒绝删除。");
            var source = PathSafety.RequireInside(context.ServerPaths.SaveRoot, slots.GetSlotPath(registry, slotId));
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"找不到停放存档：{source}");
            BackupService.RejectReparsePoints(source);
            slots.ValidateSlot(registry, slotId);
            var snapshot = await backups.CreateDeleteWorldSnapshotAsync(registry, slot, cancellationToken);
            backups.ValidateDeleteWorldSnapshot(snapshot, slot);
            checkpoint?.Invoke(DeleteWorldCheckpoint.SnapshotCreated);
            if (IsServerRunning()) throw new InvalidOperationException("创建快照后检测到服务器启动，存档未删除。");
            if (File.Exists(context.StatePaths.PendingOperationPath)) throw new InvalidOperationException("创建快照后出现其他事务，存档未删除。");
            await identity.AuditOrThrowAsync(await slots.LoadAsync(cancellationToken), cancellationToken: cancellationToken);

            var quarantine = PathSafety.RequireInside(context.ServerPaths.SaveRoot,
                Path.Combine(context.ServerPaths.SaveRoot, $".pal-delete-{slot.Id:D3}-{Guid.NewGuid():N}"));
            var operation = new PendingOperation
            {
                Type = "DeleteWorld", FromSlot = slot.Id, FromWorldUid = slot.WorldGuid,
                ToSlot = registry.ActiveSlotId, ToWorldUid = slots.GetSlot(registry, registry.ActiveSlotId).WorldGuid,
                RecoverySnapshotPath = snapshot, SourcePath = source, QuarantinePath = quarantine
            };
            await journal.WriteAsync(operation, "Prepared", cancellationToken);
            try
            {
                if (IsServerRunning()) throw new InvalidOperationException("服务器已启动，已取消删除。");
                Directory.Move(source, quarantine); // same SaveGames volume
                await journal.WriteAsync(operation, "WorldStaged", cancellationToken);
                checkpoint?.Invoke(DeleteWorldCheckpoint.WorldStaged);

                await RemoveMetadataAsync(registry, slot, cancellationToken);
                await journal.WriteAsync(operation, "MetadataUpdated", cancellationToken);
                checkpoint?.Invoke(DeleteWorldCheckpoint.MetadataUpdated);
                await identity.AuditOrThrowAsync(await slots.LoadAsync(cancellationToken), checkInterruptedOperation: false, cancellationToken: cancellationToken);
                if (IsServerRunning()) throw new InvalidOperationException("服务器已启动，已取消删除。");
                checkpoint?.Invoke(DeleteWorldCheckpoint.BeforePurge);
                BackupService.RejectReparsePoints(quarantine);
                if (purge is null) Directory.Delete(quarantine, recursive: true);
                else purge(quarantine);
                if (Directory.Exists(quarantine)) throw new IOException("删除后停放存档仍然存在。");
                await journal.WriteAsync(operation, "Completed", CancellationToken.None);
                journal.Complete();
                await log.WriteAsync($"Deleted parked save {slot.Id} ({slot.Tag}), world {slot.WorldGuid}; recovery snapshot: {snapshot}", CancellationToken.None);
                return snapshot;
            }
            catch (Exception error)
            {
                var recoveryErrors = new List<Exception>();
                try
                {
                    backups.ValidateDeleteWorldSnapshot(snapshot, slot);
                    if (Directory.Exists(source) && Directory.Exists(quarantine))
                        throw new IOException("原停放路径与事务暂存目录同时存在；来源不明确，已保留事务记录和两个目录供人工核查。");
                    if (Directory.Exists(quarantine))
                    {
                        BackupService.RejectReparsePoints(quarantine);
                        Directory.Delete(quarantine, recursive: true);
                    }
                    if (!Directory.Exists(source)) backups.RestoreDeletedWorldData(snapshot, slot, source);
                    else if (!backups.DeletedWorldDataMatches(snapshot, slot, source))
                        throw new IOException("原停放路径的内容与保护快照不符；已保留事务记录供人工核查。");
                }
                catch (Exception restoreError) { recoveryErrors.Add(restoreError); }
                try { await RestoreMetadataAsync(snapshot, slot, CancellationToken.None); }
                catch (Exception restoreError) { recoveryErrors.Add(restoreError); }
                try { await identity.AuditOrThrowAsync(await slots.LoadAsync(), checkInterruptedOperation: false); }
                catch (Exception restoreError) { recoveryErrors.Add(restoreError); }
                if (recoveryErrors.Count == 0) journal.Complete();
                if (recoveryErrors.Count > 0)
                    throw new AggregateException($"删除失败且自动恢复未能完全验证；事务记录和保护快照已保留：{snapshot}", new[] { error }.Concat(recoveryErrors));
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RemoveMetadataAsync(SaveSlotRegistry registry, SaveSlot slot, CancellationToken cancellationToken)
    {
        var buildPath = context.StatePaths.BuildStatePath;
        if (File.Exists(buildPath))
        {
            var build = JsonSerializer.Deserialize<BuildStateDocument>(await File.ReadAllBytesAsync(buildPath, cancellationToken), SafeFileService.DefaultJsonOptions)
                ?? throw new InvalidOperationException("版本状态文件为空。");
            foreach (var key in build.Worlds.Keys.Where(key => string.Equals(key, slot.WorldGuid, StringComparison.OrdinalIgnoreCase)).ToArray())
                build.Worlds.Remove(key);
            await files.WriteJsonAsync(buildPath, build, keepPrevious: true, cancellationToken: cancellationToken);
        }
        var activityPath = context.StatePaths.PlayerActivityPath;
        if (File.Exists(activityPath))
        {
            var activity = JsonSerializer.Deserialize<PlayerActivityDocument>(await File.ReadAllBytesAsync(activityPath, cancellationToken), SafeFileService.DefaultJsonOptions)
                ?? throw new InvalidOperationException("玩家活动文件为空。");
            foreach (var key in activity.Worlds.Keys.Where(key => string.Equals(key, slot.WorldGuid, StringComparison.OrdinalIgnoreCase)).ToArray())
                activity.Worlds.Remove(key);
            await files.WriteJsonAsync(activityPath, activity, keepPrevious: true, cancellationToken: cancellationToken);
        }
        var profile = context.StatePaths.WorldProfilePath(slot.Id);
        if (File.Exists(profile)) File.Delete(profile);
        registry.Slots.RemoveAll(item => item.Id == slot.Id);
        await slots.SaveAsync(registry, cancellationToken); // registry is the final metadata commit
        if (File.Exists(profile + ".previous")) File.Delete(profile + ".previous");
        foreach (var path in new[] { context.StatePaths.RegistryPath, buildPath, activityPath }.Where(File.Exists))
            File.Copy(path, path + ".previous", overwrite: true);
    }

    private async Task RestoreMetadataAsync(string snapshot, SaveSlot slot, CancellationToken cancellationToken)
    {
        backups.ValidateDeleteWorldSnapshot(snapshot, slot);
        var currentPaths = new[]
        {
            context.StatePaths.RegistryPath, context.StatePaths.BuildStatePath,
            context.StatePaths.PlayerActivityPath, context.StatePaths.WorldProfilePath(slot.Id)
        };
        foreach (var destination in currentPaths.Concat(currentPaths.Select(path => path + ".previous")))
        {
            var source = Path.Combine(snapshot, "metadata", Path.GetFileName(destination));
            if (File.Exists(source)) await files.WriteBytesAsync(destination, await File.ReadAllBytesAsync(source, cancellationToken), keepPrevious: false, cancellationToken: cancellationToken);
            else if (File.Exists(destination)) File.Delete(destination);
        }
    }
}
