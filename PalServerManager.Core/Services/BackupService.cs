using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed partial class BackupService(PalContext context, ServerProcessService processes, SaveSlotService slots, WorldSettingsService worldSettings, SafeFileService files, LoggingService log,
    Action<BackupCheckpoint, string>? checkpoint = null, Func<string, long, bool>? capacityProbe = null, Func<string, string>? volumeIdentity = null)
{
    public async Task<IReadOnlyList<BackupEntry>> ListAsync(SaveSlotRegistry registry, CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            var worldGuid = slots.GetSlot(registry, registry.ActiveSlotId).WorldGuid;
            var worldPath = GetActiveWorldPath(registry);
            var sources = new[]
            {
                (BackupKind.Native, "游戏自动备份", context.ServerPaths.BuiltInBackupRoot(worldPath)),
                (BackupKind.Protection, "回档前保护", context.StatePaths.WorldBackupRoot(worldGuid))
            };
            var result = new List<BackupEntry>();
            foreach (var source in sources)
            {
                if (!Directory.Exists(source.Item3)) continue;
                foreach (var folder in Directory.EnumerateDirectories(source.Item3))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(folder);
                    var kind = source.Item1 == BackupKind.Native ? BackupKind.Native : name.StartsWith("pre-update-", StringComparison.OrdinalIgnoreCase) ? BackupKind.PreUpdate : BackupKind.Protection;
                    if (!IsValidName(kind, name) || !IsValidBackup(folder)) continue;
                    var capturedAt = new DateTimeOffset(Directory.GetLastWriteTime(folder));
                    if (source.Item1 == BackupKind.Native && DateTime.TryParseExact(name, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        capturedAt = new DateTimeOffset(parsed);
                    var playerCount = CountPlayerSaves(folder);
                    var bytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(path =>
                    {
                        try { return new FileInfo(path).Length; } catch { return 0L; }
                    });
                    var label = kind == BackupKind.PreUpdate ? "更新前保护" : source.Item2;
                    result.Add(new BackupEntry(kind, label, name, capturedAt, capturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), playerCount, Math.Round(bytes / (1024d * 1024d), 2), folder)
                    { HasManagerManifest = File.Exists(Path.Combine(folder, "manager-manifest.json")) });
                }
            }
            return (IReadOnlyList<BackupEntry>)result.OrderByDescending(entry => entry.CapturedAt).ToArray();
        }, cancellationToken);

    public async Task<string> RestoreAsync(SaveSlotRegistry registry, BackupEntry backup, CancellationToken cancellationToken = default)
    {
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("回档前必须先关闭服务器。");
        if (File.Exists(context.StatePaths.PendingOperationPath)) throw new InvalidOperationException("存在未完成的存档事务，已拒绝回档。");
        slots.ValidateSlot(registry, registry.ActiveSlotId);
        var worldGuid = slots.GetSlot(registry, registry.ActiveSlotId).WorldGuid;
        var worldPath = GetActiveWorldPath(registry);
        var sourcePath = ValidateSource(worldPath, worldGuid, backup);
        ValidateSnapshotManifest(sourcePath, registry, registry.ActiveSlotId, required: backup.Kind != BackupKind.Native);
        var protectionRoot = PathSafety.RequireInside(context.StatePaths.BackupRoot, context.StatePaths.WorldBackupRoot(worldGuid));
        checkpoint?.Invoke(BackupCheckpoint.BeforeProtection, worldPath);
        var protectionPath = await CreateVerifiedSnapshotAsync(registry, registry.ActiveSlotId, worldPath, protectionRoot,
            $"before-rollback-{DateTime.Now:yyyyMMdd-HHmmss-fff}", "before-rollback", "", cancellationToken);
        var profilePath = worldSettings.GetProfilePath(registry.ActiveSlotId);
        var originalProfile = File.Exists(profilePath) ? await File.ReadAllBytesAsync(profilePath, cancellationToken) : null;

        // Both staging directories are under SaveGames, so every later rename stays on
        // the Palworld volume. The authoritative world was copied and hash-verified first.
        var token = Guid.NewGuid().ToString("N");
        var candidate = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, $".pal-restore-candidate-{token}"));
        var oldPayload = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, $".pal-restore-old-{token}"));
        var failedPayload = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.SaveRoot, $".pal-restore-failed-{token}"));
        var movedOriginal = new List<string>();
        var installed = new List<string>();
        var journal = new OperationJournalService(context, files);
        var operation = new PendingOperation { Type = "Restore", FromSlot = registry.ActiveSlotId, FromWorldUid = worldGuid,
            ToSlot = registry.ActiveSlotId, ToWorldUid = worldGuid };
        await journal.WriteAsync(operation, "ProtectionVerified", cancellationToken);
        await log.WriteAsync($"Restoring save {registry.ActiveSlotId} from {backup.Kind} backup '{backup.Name}'. Protection snapshot: {protectionPath}", cancellationToken);
        try
        {
            EnsureCapacity(context.ServerPaths.SaveRoot, PayloadBytes(sourcePath));
            Directory.CreateDirectory(candidate);
            CopyPayload(sourcePath, candidate, cancellationToken);
            checkpoint?.Invoke(BackupCheckpoint.AfterCopyBeforeVerify, candidate);
            VerifyPayload(sourcePath, candidate);
            VerifyPayload(worldPath, protectionPath);
            await journal.WriteAsync(operation, "CandidateVerified", cancellationToken);
            checkpoint?.Invoke(BackupCheckpoint.BeforeActiveReplace, worldPath);
            Directory.CreateDirectory(oldPayload);
            foreach (var item in EnumeratePayload(worldPath).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(item);
                MoveItem(item, Path.Combine(oldPayload, name));
                movedOriginal.Add(name);
                checkpoint?.Invoke(BackupCheckpoint.DuringRestore, name);
            }
            await journal.WriteAsync(operation, "OriginalStaged", cancellationToken);
            foreach (var item in Directory.EnumerateFileSystemEntries(candidate).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(item);
                MoveItem(item, Path.Combine(worldPath, name));
                installed.Add(name);
                checkpoint?.Invoke(BackupCheckpoint.DuringRestore, name);
            }
            VerifyPayload(sourcePath, worldPath);
            if (backup.RestoreWorldSettings) await RestoreProfileFromManifestAsync(registry, sourcePath, cancellationToken);
            await journal.WriteAsync(operation, "WorldVerified", cancellationToken);
        }
        catch (Exception restoreError)
        {
            try
            {
                checkpoint?.Invoke(BackupCheckpoint.DuringRecovery, worldPath);
                if (installed.Count > 0) Directory.CreateDirectory(failedPayload);
                foreach (var name in installed)
                    if (File.Exists(Path.Combine(worldPath, name)) || Directory.Exists(Path.Combine(worldPath, name)))
                        MoveItem(Path.Combine(worldPath, name), Path.Combine(failedPayload, name));
                foreach (var name in movedOriginal)
                    MoveItem(Path.Combine(oldPayload, name), Path.Combine(worldPath, name));
                VerifyPayload(protectionPath, worldPath);
                if (backup.RestoreWorldSettings)
                {
                    if (originalProfile is null) { if (File.Exists(profilePath)) File.Delete(profilePath); }
                    else await files.WriteBytesAsync(profilePath, originalProfile, keepPrevious: true, cancellationToken: CancellationToken.None);
                }
                if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
                if (Directory.Exists(oldPayload)) Directory.Delete(oldPayload, true);
                if (Directory.Exists(failedPayload)) Directory.Delete(failedPayload, true);
                journal.Complete();
                await log.WriteAsync($"Restore failed; original world verified and recovered. Protection snapshot: {protectionPath}", CancellationToken.None);
                throw new InvalidOperationException($"回档失败，原存档已恢复：{restoreError.Message}", restoreError);
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error.InnerException, restoreError)) { throw; }
            catch (Exception recoveryError)
            {
                await log.WriteAsync($"Restore and recovery failed; journal and staging preserved: {recoveryError.Message}", CancellationToken.None);
                throw new AggregateException("回档与自动恢复均失败，事务记录和临时目录已保留；禁止启动服务器。", restoreError, recoveryError);
            }
        }
        // Cleanup is deliberately outside the rollback catch. A cleanup failure must
        // never attempt recovery from a partly deleted old-payload directory.
        Directory.Delete(candidate, true);
        Directory.Delete(oldPayload, true);
        journal.Complete();
        await log.WriteAsync($"Save {registry.ActiveSlotId} was restored successfully. Existing WorldOption files and native backup history were preserved.", cancellationToken);
        return protectionPath;
    }

    public async Task<BackupEntry> CreatePreUpdateSnapshotAsync(SaveSlotRegistry registry, int slotId, string buildId, CancellationToken cancellationToken = default)
    {
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("更新前保护快照必须在服务器启动前创建。");
        slots.ValidateSlot(registry, slotId);
        var slot = slots.GetSlot(registry, slotId);
        var worldPath = Path.Combine(slots.GetSlotPath(registry, slotId), slot.WorldGuid);
        var root = PathSafety.RequireInside(context.StatePaths.BackupRoot, context.StatePaths.WorldBackupRoot(slot.WorldGuid));
        var name = $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var destination = await CreateVerifiedSnapshotAsync(registry, slotId, worldPath, root, name, "pre-update", buildId, cancellationToken);
        var size = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) / (1024d * 1024d);
        var count = CountPlayerSaves(destination);
        await log.WriteAsync($"Created pre-update snapshot for save {slot.Id}, world {slot.WorldGuid}, build {buildId}: {destination}", cancellationToken);
        return new BackupEntry(BackupKind.PreUpdate, "更新前保护", name, DateTimeOffset.Now, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), count, size, destination) { HasManagerManifest = true };
    }

    public async Task<string> CreateDeleteWorldSnapshotAsync(SaveSlotRegistry registry, SaveSlot slot, CancellationToken cancellationToken = default)
    {
        if (processes.GetSnapshot().IsRunning) throw new InvalidOperationException("服务器运行时不能创建删除保护快照。");
        if (slot.Id == registry.ActiveSlotId) throw new InvalidOperationException("不能删除当前存档。");
        var source = PathSafety.RequireInside(context.ServerPaths.SaveRoot, slots.GetSlotPath(registry, slot.Id));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"找不到停放存档：{source}");
        RejectReparsePoints(source);
        var root = PathSafety.RequireInside(context.StatePaths.BackupRoot, context.StatePaths.WorldBackupRoot(slot.WorldGuid));
        var name = $"before-delete-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var final = PathSafety.RequireInside(root, Path.Combine(root, name));
        var incomplete = PathSafety.RequireInside(root, Path.Combine(root, $".incomplete-{name}"));
        var metadata = DeleteMetadataPaths(slot.Id);
        var bytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
                    + metadata.Where(File.Exists).Sum(path => new FileInfo(path).Length);
        EnsureCapacity(root, bytes);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(incomplete);
        try
        {
            CopyItem(source, Path.Combine(incomplete, "parked"), cancellationToken);
            var metadataRoot = Path.Combine(incomplete, "metadata");
            Directory.CreateDirectory(metadataRoot);
            foreach (var path in metadata.Where(File.Exists)) CopyItem(path, Path.Combine(metadataRoot, Path.GetFileName(path)), cancellationToken);
            checkpoint?.Invoke(BackupCheckpoint.AfterCopyBeforeVerify, incomplete);
            var expected = HashDeleteSources(source, metadata);
            var actual = HashDeleteSnapshot(incomplete);
            if (!HashMapsEqual(expected, actual)) throw new InvalidOperationException("删除保护快照的文件校验失败；存档未删除。");
            var manifest = new DeleteWorldSnapshotManifest
            {
                SlotId = slot.Id, Label = slot.Tag, WorldUid = slot.WorldGuid, ParkedFolder = slot.ParkedFolder,
                FilesSha256 = actual
            };
            await files.WriteJsonAsync(Path.Combine(incomplete, "manager-delete-manifest.json"), manifest, keepPrevious: false, cancellationToken: cancellationToken);
            ValidateDeleteWorldSnapshot(incomplete, slot);
            checkpoint?.Invoke(BackupCheckpoint.BeforePublish, incomplete);
            MoveItem(incomplete, final);
            await log.WriteAsync($"Created verified delete snapshot for save {slot.Id}, world {slot.WorldGuid}: {final}", cancellationToken);
            return final;
        }
        catch
        {
            // Incomplete copies are deliberately retained for diagnosis; they
            // are never presented as a valid protective snapshot.
            throw;
        }
    }

    public void ValidateDeleteWorldSnapshot(string snapshot, SaveSlot slot)
    {
        var root = PathSafety.RequireInside(context.StatePaths.WorldBackupRoot(slot.WorldGuid), snapshot);
        var path = Path.Combine(root, "manager-delete-manifest.json");
        var manifest = JsonSerializer.Deserialize<DeleteWorldSnapshotManifest>(File.ReadAllBytes(path), SafeFileService.DefaultJsonOptions)
            ?? throw new InvalidOperationException("删除保护快照清单为空。");
        if (manifest.SlotId != slot.Id || !string.Equals(manifest.WorldUid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ParkedFolder, slot.ParkedFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("删除保护快照与所选存档身份不匹配。");
        if (!HashMapsEqual(manifest.FilesSha256, HashDeleteSnapshot(root)))
            throw new InvalidOperationException("删除保护快照清单的 SHA-256 校验失败。");
    }

    public void RestoreDeletedWorldData(string snapshot, SaveSlot slot, string destination)
    {
        ValidateDeleteWorldSnapshot(snapshot, slot);
        var safeDestination = PathSafety.RequireInside(context.ServerPaths.SaveRoot, destination);
        if (Directory.Exists(safeDestination) || File.Exists(safeDestination)) throw new IOException("恢复位置已存在，不能覆盖其他存档。");
        var temporary = PathSafety.RequireInside(context.ServerPaths.SaveRoot,
            Path.Combine(context.ServerPaths.SaveRoot, $".pal-delete-restore-{Guid.NewGuid():N}"));
        CopyItem(Path.Combine(snapshot, "parked"), temporary, CancellationToken.None);
        if (!HashMapsEqual(HashDirectory(Path.Combine(snapshot, "parked")), HashDirectory(temporary)))
            throw new InvalidOperationException("删除事务回滚时，世界数据复制校验失败。");
        Directory.Move(temporary, safeDestination);
    }

    public bool DeletedWorldDataMatches(string snapshot, SaveSlot slot, string destination)
    {
        ValidateDeleteWorldSnapshot(snapshot, slot);
        var safeDestination = PathSafety.RequireInside(context.ServerPaths.SaveRoot, destination);
        if (!Directory.Exists(safeDestination)) return false;
        RejectReparsePoints(safeDestination);
        return HashMapsEqual(HashDirectory(Path.Combine(snapshot, "parked")), HashDirectory(safeDestination));
    }

    private string[] DeleteMetadataPaths(int slotId)
    {
        var paths = new[]
        {
            context.StatePaths.RegistryPath, context.StatePaths.BuildStatePath,
            context.StatePaths.PlayerActivityPath, context.StatePaths.WorldProfilePath(slotId)
        };
        return paths.Concat(paths.Select(path => path + ".previous")).ToArray();
    }

    public static void RejectReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("存档包含符号链接或重解析点，已拒绝删除。");
            if ((attributes & FileAttributes.Directory) == 0) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly)) pending.Push(child);
        }
    }

    private static Dictionary<string, string> HashDeleteSources(string parked, IEnumerable<string> metadata)
    {
        var map = HashDirectory(parked).ToDictionary(pair => "parked/" + pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var path in metadata.Where(File.Exists)) map["metadata/" + Path.GetFileName(path)] = HashFile(path);
        return map;
    }

    private static Dictionary<string, string> HashDeleteSnapshot(string root)
    {
        var map = HashDirectory(Path.Combine(root, "parked")).ToDictionary(pair => "parked/" + pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var metadata = Path.Combine(root, "metadata");
        if (Directory.Exists(metadata))
            foreach (var path in Directory.EnumerateFiles(metadata, "*", SearchOption.TopDirectoryOnly))
                map["metadata/" + Path.GetFileName(path)] = HashFile(path);
        return map;
    }

    private static Dictionary<string, string> HashDirectory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'), HashFile, StringComparer.OrdinalIgnoreCase);

    private static bool HashMapsEqual(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var hash) &&
            string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase));

    private async Task<string> CreateVerifiedSnapshotAsync(SaveSlotRegistry registry, int slotId, string worldPath,
        string root, string name, string type, string buildId, CancellationToken cancellationToken)
    {
        if (!IsValidBackup(worldPath)) throw new InvalidOperationException("当前世界缺少非空 Level.sav，或 Players 路径不是目录。");
        EnsureCapacity(root, PayloadBytes(worldPath));
        Directory.CreateDirectory(root);
        var final = PathSafety.RequireInside(root, Path.Combine(root, name));
        if (Directory.Exists(final)) throw new IOException($"保护快照已存在：{final}");
        var incomplete = PathSafety.RequireInside(root, Path.Combine(root, $".incomplete-{name}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(incomplete);
        // An interrupted or failed copy remains visibly incomplete and is never
        // listed as a valid backup. The authoritative world is untouched.
        CopyPayload(worldPath, incomplete, cancellationToken);
        // A freshly created world has no player saves and may not have a Players directory yet.
        Directory.CreateDirectory(Path.Combine(incomplete, "Players"));
        checkpoint?.Invoke(BackupCheckpoint.AfterCopyBeforeVerify, incomplete);
        VerifyPayload(worldPath, incomplete);
        await CreateManifestAsync(registry, slotId, worldPath, type, buildId, incomplete, cancellationToken);
        ValidateSnapshotManifest(incomplete, registry, slotId, required: true);
        checkpoint?.Invoke(BackupCheckpoint.BeforePublish, incomplete);
        MoveItem(incomplete, final); // same Manager-state volume; publish only after verification
        return final;
    }

    private void EnsureCapacity(string destination, long payloadBytes)
    {
        var required = checked(payloadBytes + Math.Max(8L * 1024 * 1024, payloadBytes / 20));
        var available = capacityProbe?.Invoke(destination, required) ??
            new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace >= required;
        if (!available) throw new IOException($"备份目标空间不足：至少需要 {required:N0} 字节：{destination}");
    }

    private void CopyPayload(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var item in EnumeratePayload(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyItem(item, Path.Combine(destination, Path.GetFileName(item)), cancellationToken);
        }
    }

    private void VerifyPayload(string source, string destination)
    {
        if (!IsValidBackup(source) || !IsValidBackup(destination)) throw new InvalidOperationException("快照缺少非空 Level.sav，或 Players 路径不是目录。");
        var expected = PayloadHashes(source);
        var actual = PayloadHashes(destination);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new InvalidOperationException("快照文件数量或 SHA-256 校验不匹配；原存档未被替换。");
    }

    private void ValidateSnapshotManifest(string path, SaveSlotRegistry registry, int slotId, bool required)
    {
        var manifestPath = Path.Combine(path, "manager-manifest.json");
        if (!File.Exists(manifestPath))
        {
            if (required) throw new InvalidOperationException("管理器备份缺少清单。");
            return;
        }
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllBytes(manifestPath), SafeFileService.DefaultJsonOptions)
            ?? throw new InvalidOperationException("管理器备份清单为空。");
        var slot = slots.GetSlot(registry, slotId);
        if (manifest.SlotId != slot.Id || !string.Equals(manifest.WorldUid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份清单与当前存档不匹配。");
        if (manifest.WorldSettingsProfile is null ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest.WorldSettingsProfile, SafeFileService.DefaultJsonOptions))),
                manifest.WorldSettingsProfileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份清单的世界设置校验失败。");
        if (manifest.PayloadSha256 is not null)
        {
            var actual = PayloadHashes(path);
            if (actual.Count != manifest.PayloadSha256.Count || actual.Any(pair =>
                !manifest.PayloadSha256.TryGetValue(pair.Key, out var hash) || !string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("备份清单的文件哈希不匹配。");
            if (manifest.PayloadBytes != PayloadBytes(path)) throw new InvalidOperationException("备份清单的文件大小不匹配。");
        }
    }

    private static long PayloadBytes(string root) => PayloadFiles(root).Sum(path => new FileInfo(path).Length);

    private static Dictionary<string, string> PayloadHashes(string root) => PayloadFiles(root).ToDictionary(
        path => Path.GetRelativePath(root, path).Replace('\\', '/'),
        path => HashFile(path), StringComparer.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static IEnumerable<string> PayloadFiles(string root) => EnumeratePayload(root).SelectMany(path =>
        File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));

    private string GetActiveWorldPath(SaveSlotRegistry registry)
    {
        var worldGuid = slots.GetSlot(registry, registry.ActiveSlotId).WorldGuid;
        var path = PathSafety.RequireInside(context.ServerPaths.SaveRoot, Path.Combine(context.ServerPaths.ActivePath, worldGuid));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"找不到当前世界文件夹：{path}");
        return path;
    }

    private string ValidateSource(string worldPath, string worldGuid, BackupEntry backup)
    {
        if (!IsValidName(backup.Kind, backup.Name)) throw new InvalidOperationException("备份名称无效。");
        var root = backup.Kind == BackupKind.Native ? context.ServerPaths.BuiltInBackupRoot(worldPath) : context.StatePaths.WorldBackupRoot(worldGuid);
        var source = PathSafety.RequireInside(root, Path.Combine(root, backup.Name));
        if (!IsValidBackup(source)) throw new InvalidOperationException("所选备份不完整，必须包含非空 Level.sav。");
        return source;
    }

    private static bool IsValidName(BackupKind kind, string name) =>
        kind == BackupKind.Native ? NativeNameRegex().IsMatch(name) : kind == BackupKind.PreUpdate ? PreUpdateNameRegex().IsMatch(name) : ProtectionNameRegex().IsMatch(name);

    private static bool IsValidBackup(string path) =>
        File.Exists(Path.Combine(path, "Level.sav")) && new FileInfo(Path.Combine(path, "Level.sav")).Length > 0 &&
        !File.Exists(Path.Combine(path, "Players"));

    private static int CountPlayerSaves(string root)
    {
        var players = Path.Combine(root, "Players");
        return Directory.Exists(players)
            ? Directory.EnumerateFiles(players, "*.sav", SearchOption.TopDirectoryOnly)
                .Count(path => !Path.GetFileName(path).EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase))
            : 0;
    }

    private static IEnumerable<string> EnumeratePayload(string worldPath) =>
        Directory.EnumerateFileSystemEntries(worldPath).Where(path => !ShouldPreserve(Path.GetFileName(path)) &&
            !string.Equals(Path.GetFileName(path), "manager-manifest.json", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldPreserve(string name) =>
        string.Equals(name, "backup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "WorldOption.sav", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "WorldOptions.sav", StringComparison.OrdinalIgnoreCase);

    private async Task CreateManifestAsync(SaveSlotRegistry registry, int slotId, string worldPath, string type, string buildId, string destination, CancellationToken cancellationToken)
    {
        var slot = slots.GetSlot(registry, slotId);
        var profile = await worldSettings.LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, SafeFileService.DefaultJsonOptions);
        var manifest = new SnapshotManifest
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            SnapshotType = type,
            ManagerVersion = ManagerProduct.Version,
            PalServerBuildId = buildId,
            SlotId = slot.Id,
            WorldUid = slot.WorldGuid,
            ManagementTag = slot.Tag,
            WorldSettingsProfile = profile,
            WorldSettingsProfileHash = Convert.ToHexString(SHA256.HashData(profileBytes)),
            PlayerSaveCount = CountPlayerSaves(worldPath),
            PayloadSha256 = PayloadHashes(destination),
            PayloadBytes = PayloadBytes(destination)
        };
        await files.WriteJsonAsync(Path.Combine(destination, "manager-manifest.json"), manifest, keepPrevious: false, cancellationToken: cancellationToken);
    }

    private async Task RestoreProfileFromManifestAsync(SaveSlotRegistry registry, string sourcePath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(sourcePath, "manager-manifest.json");
        if (!File.Exists(path)) throw new InvalidOperationException("该备份没有管理器清单，无法恢复历史世界设置。");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SafeFileService.DefaultJsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("备份清单为空。");
        var slot = slots.GetSlot(registry, registry.ActiveSlotId);
        if (manifest.SlotId != slot.Id || !string.Equals(manifest.WorldUid, slot.WorldGuid, StringComparison.OrdinalIgnoreCase) || manifest.WorldSettingsProfile is null)
            throw new InvalidOperationException("备份清单不属于当前存档/世界 UID，已阻止恢复历史设置。");
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(manifest.WorldSettingsProfile, SafeFileService.DefaultJsonOptions);
        var actualHash = Convert.ToHexString(SHA256.HashData(profileBytes));
        if (!string.Equals(actualHash, manifest.WorldSettingsProfileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份清单中的世界设置校验值不匹配，已阻止恢复。");
        await worldSettings.RestoreProfileSnapshotAsync(slot, manifest.WorldSettingsProfile, cancellationToken);
    }

    private void MoveItem(string source, string destination)
    {
        var sourceVolume = volumeIdentity?.Invoke(source) ?? Path.GetPathRoot(Path.GetFullPath(source));
        var destinationVolume = volumeIdentity?.Invoke(destination) ?? Path.GetPathRoot(Path.GetFullPath(destination));
        if (!string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("禁止跨卷移动存档或保护快照；必须复制并验证。");
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private void CopyItem(string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(source, destination, true);
            checkpoint?.Invoke(BackupCheckpoint.DuringCopy, destination);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, true);
            checkpoint?.Invoke(BackupCheckpoint.DuringCopy, target);
        }
    }

    [GeneratedRegex("^\\d{4}\\.\\d{2}\\.\\d{2}-\\d{2}\\.\\d{2}\\.\\d{2}$", RegexOptions.CultureInvariant)] private static partial Regex NativeNameRegex();
    [GeneratedRegex("^before-rollback-\\d{8}-\\d{6}-\\d{3}$", RegexOptions.CultureInvariant)] private static partial Regex ProtectionNameRegex();
    [GeneratedRegex("^pre-update-\\d{8}-\\d{6}-\\d{3}$", RegexOptions.CultureInvariant)] private static partial Regex PreUpdateNameRegex();
}
