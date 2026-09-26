using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class ServerManagerService(
    PalContext context, ServerProcessService processes, PalRestApiService rest, SaveSlotService slots,
    WorldSettingsService worldSettings, KeepAwakeService keepAwake, BackupService backups,
    WorldIdentityAuditService identity, WorldOptionService worldOptions, PalServerBuildService builds,
    OperationJournalService journal, SafeFileService safeFiles, IPowerService power, LoggingService log)
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    public Func<WorldOptionConflict, Task<WorldOptionDecision>>? ResolveWorldOptionAsync { get; set; }
    public Func<SaveSlot, string, Task<UpdateProtectionDecision>>? ResolveUpdateProtectionAsync { get; set; }
    public bool IsBusy => _operationGate.CurrentCount == 0;

    public Task SaveAsync(CancellationToken ct = default) => RunExclusiveAsync("保存世界", async token =>
    { RequireRunning(); await rest.SaveAsync(token); await log.WriteAsync("World save completed.", token); }, ct);

    public Task SaveAndStopAsync(bool powerOff, CancellationToken ct = default) => RunExclusiveAsync("保存并关服", async token =>
    {
        var stopped = await SaveAndStopCoreAsync(token);
        if (!powerOff) return;
        if (!stopped) throw new InvalidOperationException("服务器没有运行，已取消关闭电脑。");
        power.ScheduleShutdown();
    }, ct);

    public Task ForceStopAsync(CancellationToken ct = default) => RunExclusiveAsync("不保存强制关服", token => processes.ForceStopAsync(token), ct);
    public Task RestartAsync(CancellationToken ct = default) => RunExclusiveAsync("保存并重启", async token =>
    {
        if (!await SaveAndStopCoreAsync(token, "Server restart in 10 seconds. The world will be saved first.")) throw new InvalidOperationException("服务器没有运行，已取消重启。");
        await StartActiveTransactionalAsync(token);
    }, ct);
    public Task StartActiveAsync(CancellationToken ct = default) => RunExclusiveAsync("启动服务器", StartActiveTransactionalAsync, ct);

    public Task SwitchAndStartAsync(int slotId, CancellationToken ct = default) => RunExclusiveAsync("切换存档并启动服务器", async token =>
    {
        var original = await slots.LoadAsync(token);
        await identity.AuditOrThrowAsync(original, cancellationToken: token);
        var current = slots.GetSlot(original, original.ActiveSlotId);
        var target = slots.GetSlot(original, slotId);
        if (current.Id == target.Id) { await StartActiveTransactionalAsync(token); return; }
        await PrepareTargetAsync(original, target, token);
        var runtimeIni = await File.ReadAllBytesAsync(context.ServerPaths.SettingsPath, token);
        var userIni = await File.ReadAllBytesAsync(context.ServerPaths.UserSettingsPath, token);
        var op = new PendingOperation { Type = "SwitchAndStart", FromSlot = current.Id, FromWorldUid = current.WorldGuid, ToSlot = target.Id, ToWorldUid = target.WorldGuid };
        var switched = false;
        await journal.WriteAsync(op, "Prepared", token);
        try
        {
            await worldSettings.ApplyProfileToRuntimeAsync(target.Id, target.WorldGuid, token);
            await journal.WriteAsync(op, "ProfileApplied", token);
            await slots.SwitchAsync(original, target.Id, token);
            switched = true;
            await journal.WriteAsync(op, "SaveMoved", token);
            await StartProcessAndVerifyAsync(target, token);
            await journal.WriteAsync(op, "WorldVerified", token);
            await builds.MarkSuccessfulAsync(target, await builds.GetCurrentBuildAsync(token), token);
            await identity.AuditOrThrowAsync(await slots.LoadAsync(token), checkInterruptedOperation: false, cancellationToken: token);
            await journal.WriteAsync(op, "Completed", token);
            journal.Complete();
        }
        catch (Exception originalError)
        {
            var recovery = new List<Exception>();
            await StopWithoutSavingForRecoveryAsync(recovery);
            if (switched)
            {
                try { await slots.SwitchAsync(await slots.LoadAsync(), current.Id); }
                catch (Exception error) { recovery.Add(error); }
            }
            await RestoreFileAsync(context.ServerPaths.SettingsPath, runtimeIni, recovery);
            await RestoreFileAsync(context.ServerPaths.UserSettingsPath, userIni, recovery);
            try { await identity.AuditOrThrowAsync(await slots.LoadAsync(), checkInterruptedOperation: false); }
            catch (Exception error) { recovery.Add(error); }
            if (recovery.Count == 0) journal.Complete();
            if (recovery.Count > 0) throw new AggregateException($"切档失败，且自动恢复未能完全验证。事务记录已保留在 {context.StatePaths.PendingOperationPath}。", new[] { originalError }.Concat(recovery));
            throw;
        }
    }, ct);

    public Task CreateAndStartAsync(string tag, NewWorldSettingsChoice settings, CancellationToken ct = default) =>
        RunExclusiveAsync("创建并启动新存档", token => CreateAndStartCoreAsync(tag, settings, token), ct);

    public Task RestoreBackupAsync(BackupService ignored, BackupEntry backup, bool restartAfterRestore, CancellationToken ct = default) => RunExclusiveAsync("回档", async token =>
    {
        var wasRunning = processes.GetSnapshot().IsRunning;
        if (wasRunning) await SaveAndStopCoreAsync(token);
        var registry = await slots.LoadAsync(token);
        await identity.AuditOrThrowAsync(registry, cancellationToken: token);
        await backups.RestoreAsync(registry, backup, token);
        if (restartAfterRestore || wasRunning) await StartActiveTransactionalAsync(token);
    }, ct);

    public async Task<T> RunExclusiveAsync<T>(string description, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        if (!await _operationGate.WaitAsync(0, ct)) throw new InvalidOperationException("另一个服务器操作正在进行，请稍候。");
        try { await log.WriteAsync($"Operation started: {description}.", ct); var result = await operation(ct); await log.WriteAsync($"Operation completed: {description}.", ct); return result; }
        catch (Exception error) { await log.WriteAsync($"Operation failed ({description}): {error.Message}", CancellationToken.None); throw; }
        finally { _operationGate.Release(); }
    }

    public Task RunExclusiveAsync(string description, Func<CancellationToken, Task> operation, CancellationToken ct = default) =>
        RunExclusiveAsync(description, async token => { await operation(token); return true; }, ct);

    private async Task StartActiveTransactionalAsync(CancellationToken ct)
    {
        var registry = await slots.LoadAsync(ct);
        await identity.AuditOrThrowAsync(registry, cancellationToken: ct);
        var active = slots.GetSlot(registry, registry.ActiveSlotId);
        await PrepareTargetAsync(registry, active, ct);
        var runtimeIni = await File.ReadAllBytesAsync(context.ServerPaths.SettingsPath, ct);
        var userIni = await File.ReadAllBytesAsync(context.ServerPaths.UserSettingsPath, ct);
        var op = new PendingOperation { Type = "Start", FromSlot = active.Id, FromWorldUid = active.WorldGuid, ToSlot = active.Id, ToWorldUid = active.WorldGuid };
        await journal.WriteAsync(op, "Prepared", ct);
        try
        {
            await worldSettings.ApplyProfileToRuntimeAsync(active.Id, active.WorldGuid, ct);
            await journal.WriteAsync(op, "ProfileApplied", ct);
            await slots.SetWorldGuidAsync(active.WorldGuid, ct);
            await journal.WriteAsync(op, "DedicatedServerNameUpdated", ct);
            await StartProcessAndVerifyAsync(active, ct);
            await journal.WriteAsync(op, "WorldVerified", ct);
            await builds.MarkSuccessfulAsync(active, await builds.GetCurrentBuildAsync(ct), ct);
            await journal.WriteAsync(op, "Completed", ct);
            journal.Complete();
        }
        catch
        {
            var recovery = new List<Exception>();
            await StopWithoutSavingForRecoveryAsync(recovery);
            await RestoreFileAsync(context.ServerPaths.SettingsPath, runtimeIni, recovery);
            await RestoreFileAsync(context.ServerPaths.UserSettingsPath, userIni, recovery);
            if (recovery.Count == 0) journal.Complete();
            if (recovery.Count > 0) throw new AggregateException("启动失败且运行配置未能完全恢复；事务记录已保留。", recovery);
            throw;
        }
    }

    private async Task PrepareTargetAsync(SaveSlotRegistry registry, SaveSlot target, CancellationToken ct)
    {
        slots.ValidateSlot(registry, target.Id);
        foreach (var conflict in worldOptions.Detect(registry, target.Id))
        {
            var decision = ResolveWorldOptionAsync is null ? WorldOptionDecision.Cancel : await ResolveWorldOptionAsync(conflict);
            if (decision != WorldOptionDecision.BackupAndDisable)
                throw new OperationCanceledException("检测到 WorldOption 文件，用户取消了启动。");
            await worldOptions.BackupAndDisableAsync(conflict, ct);
        }
        var build = await builds.GetCurrentBuildAsync(ct);
        if (!await builds.NeedsProtectionAsync(target.WorldGuid, build, ct)) return;
        var decision2 = ResolveUpdateProtectionAsync is null ? UpdateProtectionDecision.SnapshotAndStart : await ResolveUpdateProtectionAsync(target, build);
        if (decision2 == UpdateProtectionDecision.Cancel) throw new OperationCanceledException("用户取消了首次版本启动。");
        if (decision2 == UpdateProtectionDecision.SnapshotAndStart) await backups.CreatePreUpdateSnapshotAsync(registry, target.Id, build, ct);
    }

    private async Task StartProcessAndVerifyAsync(SaveSlot expected, CancellationToken ct)
    {
        await processes.StartAsync(ct);
        await keepAwake.EnsureRunningAsync(ct);
        var info = await WaitForRestReadyAsync(TimeSpan.FromSeconds(90), ct);
        if (!string.Equals(info.WorldGuid, expected.WorldGuid, StringComparison.OrdinalIgnoreCase))
        {
            await processes.ForceStopAsync(CancellationToken.None);
            throw new InvalidOperationException($"启动后的世界 UID 不匹配。预期 {expected.WorldGuid}，实际 {info.WorldGuid}；已不保存强制关闭服务器。");
        }
        await log.WriteAsync($"Server is ready: {info.ServerName} ({info.Version}), world {info.WorldGuid}.", ct);
    }

    private async Task<bool> SaveAndStopCoreAsync(CancellationToken ct, string message = "Server maintenance: shutting down in 10 seconds.")
    {
        if (!processes.GetSnapshot().IsRunning) return false;
        await rest.SaveAsync(ct); await rest.ShutdownAsync(10, message, ct); await processes.WaitForExitAsync(TimeSpan.FromMinutes(3), ct); return true;
    }

    private async Task<ServerInfo> WaitForRestReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout); Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline && processes.GetSnapshot().IsRunning)
        {
            ct.ThrowIfCancellationRequested();
            try { return await rest.GetInfoAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { last = e; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        if (!processes.GetSnapshot().IsRunning) throw new InvalidOperationException("PalServer 在 REST API 就绪前意外退出。", last);
        throw new TimeoutException($"PalServer 已运行，但 REST API 在 {(int)timeout.TotalSeconds} 秒内没有就绪。", last);
    }

    private async Task CreateAndStartCoreAsync(string tag, NewWorldSettingsChoice choice, CancellationToken ct)
    {
        var validatedTag = SaveSlotService.ValidateTag(tag);
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("创建新存档前必须关闭服务器。");
        var registry = await slots.LoadAsync(ct);
        await identity.AuditOrThrowAsync(registry, cancellationToken: ct);
        var current = slots.GetSlot(registry, registry.ActiveSlotId);
        var newId = registry.NextSlotId; while (registry.Slots.Any(slot => slot.Id == newId)) newId++;
        var parkedName = SaveSlotService.NewParkedFolderName(newId, validatedTag);
        var currentParked = Path.Combine(context.ServerPaths.SaveRoot, current.ParkedFolder);
        var transactionOld = Path.Combine(context.ServerPaths.SaveRoot, $".pal-new-old-{Guid.NewGuid():N}");
        if (Directory.Exists(currentParked)) throw new IOException($"无法停放当前存档，目标已存在：{currentParked}");
        var runtimeIni = await File.ReadAllBytesAsync(context.ServerPaths.SettingsPath, ct);
        var userIni = await File.ReadAllBytesAsync(context.ServerPaths.UserSettingsPath, ct);
        var op = new PendingOperation { Type = "CreateAndStart", FromSlot = current.Id, FromWorldUid = current.WorldGuid, ToSlot = newId };
        await journal.WriteAsync(op, "Prepared", ct); var moved = false;
        try
        {
            var values = await worldSettings.PrepareNewWorldRuntimeAsync(choice, current.Id, current.WorldGuid, ct);
            await journal.WriteAsync(op, "ProfileApplied", ct);
            Directory.Move(context.ServerPaths.ActivePath, transactionOld); Directory.CreateDirectory(context.ServerPaths.ActivePath); moved = true;
            await slots.ClearWorldGuidForGenerationAsync(ct);
            await processes.StartAsync(ct); await keepAwake.EnsureRunningAsync(ct);
            var info = await WaitForRestReadyAsync(TimeSpan.FromSeconds(120), ct);
            if (!Guid.TryParseExact(info.WorldGuid, "N", out var generatedGuid))
                throw new InvalidOperationException("PalServer 返回的新世界 UID 无效，已拒绝注册新存档。");
            var expectedUid = generatedGuid.ToString("N").ToUpperInvariant();
            if (registry.Slots.Any(slot => string.Equals(slot.WorldGuid, expectedUid, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"PalServer 生成了已登记的重复世界 UID {expectedUid}，已拒绝注册新存档。");
            // REST can become ready before PalServer writes the first world save to disk.
            await rest.SaveAsync(ct);
            await slots.WaitForActiveWorldSaveAsync(expectedUid, TimeSpan.FromSeconds(60), ct);
            var generatedUid = slots.DiscoverActiveWorldGuid();
            if (!string.Equals(info.WorldGuid, generatedUid, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"新世界的 REST UID {info.WorldGuid} 与磁盘 UID {generatedUid} 不一致。");
            await worldSettings.CommitNewWorldProfileAsync(newId, generatedUid, validatedTag, values, ct);
            Directory.Move(transactionOld, currentParked);
            registry.Slots.Add(new SaveSlot { Id = newId, Tag = validatedTag, ParkedFolder = parkedName, WorldGuid = generatedUid, CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
            registry.ActiveSlotId = newId; registry.NextSlotId = newId + 1;
            await slots.SetWorldGuidAsync(generatedUid, ct); await slots.SaveAsync(registry, ct);
            op.ToWorldUid = generatedUid;
            await identity.AuditOrThrowAsync(registry, checkInterruptedOperation: false, cancellationToken: ct);
            await builds.MarkSuccessfulAsync(slots.GetSlot(registry, newId), await builds.GetCurrentBuildAsync(ct), ct);
            await journal.WriteAsync(op, "Completed", ct); journal.Complete();
        }
        catch
        {
            var recovery = new List<Exception>(); await StopWithoutSavingForRecoveryAsync(recovery);
            if (moved)
            {
                try { if (Directory.Exists(context.ServerPaths.ActivePath)) Directory.Move(context.ServerPaths.ActivePath, Path.Combine(context.ServerPaths.SaveRoot, $"0 - failed-new-{DateTime.Now:yyyyMMdd-HHmmss}")); if (Directory.Exists(transactionOld)) Directory.Move(transactionOld, context.ServerPaths.ActivePath); }
                catch (Exception error) { recovery.Add(error); }
            }
            await RestoreFileAsync(context.ServerPaths.SettingsPath, runtimeIni, recovery); await RestoreFileAsync(context.ServerPaths.UserSettingsPath, userIni, recovery);
            try { worldSettings.DeleteProfileIfExists(newId); } catch (Exception error) { recovery.Add(error); }
            if (recovery.Count == 0) journal.Complete();
            if (recovery.Count > 0) throw new AggregateException("新世界创建失败，且旧世界未能完全自动恢复；事务记录已保留。", recovery);
            throw;
        }
    }

    private async Task StopWithoutSavingForRecoveryAsync(List<Exception> errors)
    { if (!processes.GetSnapshot().IsRunning) return; try { await processes.ForceStopAsync(CancellationToken.None); } catch (Exception e) { errors.Add(e); } }
    private async Task RestoreFileAsync(string path, byte[] content, List<Exception> errors)
    { try { await safeFiles.WriteBytesAsync(path, content, keepPrevious: true); } catch (Exception e) { errors.Add(e); } }
    private void RequireRunning() { if (!processes.GetSnapshot().IsRunning) throw new InvalidOperationException("PalServer 当前没有运行。"); }
}
