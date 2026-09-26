using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

// Attaches an existing PalServer without copying or changing any game save.
public sealed class ServerStateService(AppPaths app, SafeFileService files)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string MarkerPath(RegisteredServer server) =>
        Path.Combine(ServerStatePaths.ForRegisteredServer(app, server).StateRoot, "StateActivation.json");

    public async Task<ServerStateAssessment> AssessAsync(RegisteredServer server, CancellationToken cancellationToken = default)
    {
        if (!ServerRootService.IsValidRoot(server.ServerRoot))
            return new(ServerStateKind.InvalidServerRoot, "PalServer 路径已失效。请重新定位 PalServer.exe，或移除注册；管理状态不会删除。", server);

        var root = ServerStatePaths.ForRegisteredServer(app, server).StateRoot;
        if (File.Exists(Path.Combine(root, "PendingOperation.json")))
            return new(ServerStateKind.Interrupted, "检测到未完成的存档操作。请先检查事务记录，不能启动服务器。", server);

        var markerPath = MarkerPath(server);
        if (!File.Exists(markerPath))
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                return new(ServerStateKind.InvalidState, "已有管理状态但缺少激活记录；不会覆盖。", server);
            if (HasStagingDirectory(server))
                return new(ServerStateKind.Interrupted, "发现未完成的状态暂存目录；请先检查。", server);
            return new(ServerStateKind.NeedsInitialization, "这台 PalServer 尚未接入管理器。", server);
        }

        StateActivationRecord marker;
        try { marker = await ReadRequiredAsync<StateActivationRecord>(markerPath, cancellationToken); }
        catch (Exception error) { return new(ServerStateKind.InvalidState, $"激活记录无效：{error.Message}", server); }
        // Migrated is a persisted value in previously activated installations, not a setup path.
        if (marker.SchemaVersion != 1 || marker.ServerId != server.Id ||
            marker.Mode is not ("Migrated" or "NewWorld" or "AttachedExisting" or "EmptyNoWorld"))
            return new(ServerStateKind.InvalidState, "激活记录不属于这台服务器。", server);
        if (marker.Mode == "EmptyNoWorld")
            return new(ServerStateKind.EmptyNoWorld, "尚无已登记世界。创建首个游戏世界后重新检查。", server);

        try { await ValidateStateAsync(root, server, cancellationToken); }
        catch (Exception error) { return new(ServerStateKind.InvalidState, $"管理状态无效：{error.Message}", server); }
        return new(ServerStateKind.Ready, "管理状态已验证。", server);
    }

    public IReadOnlyList<DiscoveredWorld> DiscoverWorlds(RegisteredServer server)
    {
        if (!ServerRootService.IsValidRoot(server.ServerRoot))
            throw new InvalidOperationException("PalServer 路径无效。");
        var active = new WorldDiscoveryService(new ServerPaths(server.ServerRoot)).DiscoverActive();
        return active is null ? [] : [new DiscoveredWorld("0", active.WorldUid!, true)];
    }

    public async Task AttachAsync(RegisteredServer server, CancellationToken cancellationToken = default)
    {
        var assessment = await AssessAsync(server, cancellationToken);
        if (assessment.Kind is not (ServerStateKind.NeedsInitialization or ServerStateKind.EmptyNoWorld))
            throw new InvalidOperationException("已有管理状态或未解决的错误，不能重新接入。");
        var context = PalContext.ForRegisteredServer(app, server);
        if (new ServerProcessService(context, new LoggingService(context)).GetSnapshot().IsRunning)
            throw new InvalidOperationException("接入现有世界前请先关闭 PalServer。");
        var worlds = DiscoverWorlds(server);
        if (worlds.Count == 0)
            throw new InvalidOperationException("尚无现有世界。先让 PalServer 生成首个存档，再重新检查。");

        var root = context.StatePaths.StateRoot;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() &&
            !(assessment.Kind == ServerStateKind.EmptyNoWorld &&
              Directory.EnumerateFileSystemEntries(root).All(path =>
                  Path.GetFileName(path).Equals("StateActivation.json", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("目标管理状态目录已有内容，不能覆盖。");

        var registry = BuildRegistry(worlds.Single(), context.ServerPaths.SaveRoot);
        var stage = Path.Combine(app.ServersStateRoot, $".setup-{server.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        string? preserved = null;
        try
        {
            await files.WriteJsonAsync(Path.Combine(stage, "SaveSlots.json"), registry, cancellationToken: cancellationToken);
            await files.WriteJsonAsync(Path.Combine(stage, "ManagerSettings.json"), new ManagerSettings(), cancellationToken: cancellationToken);
            await files.WriteJsonAsync(Path.Combine(stage, "PlayerActivity.json"), new PlayerActivityDocument(), cancellationToken: cancellationToken);
            await files.WriteJsonAsync(Path.Combine(stage, "WorldBuildState.json"), new BuildStateDocument(), cancellationToken: cancellationToken);
            var stageContext = context with { StatePaths = new ServerStatePaths(stage) };
            var worldSettings = new WorldSettingsService(stageContext, new LoggingService(stageContext), files);
            var values = await worldSettings.ImportCurrentIniAsync(cancellationToken);
            foreach (var slot in registry.Slots)
            {
                await files.WriteJsonAsync(stageContext.StatePaths.WorldProfilePath(slot.Id), new WorldSettingsProfile
                {
                    SlotId = slot.Id, WorldGuid = slot.WorldGuid, Tag = slot.Tag,
                    Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                }, cancellationToken: cancellationToken);
            }
            await files.WriteJsonAsync(stageContext.StatePaths.WorldSettingsMarkerPath, new
            {
                schemaVersion = WorldSettingsService.CurrentProfileSchema,
                completedAtUtc = DateTimeOffset.UtcNow,
                source = "attach-existing-server"
            }, cancellationToken: cancellationToken);
            await ValidateStateAsync(stage, server, cancellationToken);
            await files.WriteJsonAsync(Path.Combine(stage, "StateActivation.json"), new StateActivationRecord
            {
                ServerId = server.Id, Mode = "AttachedExisting"
            }, cancellationToken: cancellationToken);
            if (Directory.Exists(root))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any())
                {
                    preserved = root + $".empty-before-attach-{Guid.NewGuid():N}";
                    Directory.Move(root, preserved);
                }
                else Directory.Delete(root);
            }
            try { Directory.Move(stage, root); }
            catch
            {
                if (preserved is not null && !Directory.Exists(root)) Directory.Move(preserved, root);
                throw;
            }
        }
        catch
        {
            // Before activation the stage is disposable Manager state; retry must remain possible.
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private bool HasStagingDirectory(RegisteredServer server) =>
        Directory.Exists(app.ServersStateRoot) &&
        Directory.EnumerateDirectories(app.ServersStateRoot, $".*-{server.Id}-*").Any();

    private static SaveSlotRegistry BuildRegistry(DiscoveredWorld active, string saveRoot)
    {
        const int id = 0;
        var tag = SaveSlotService.ValidateTag("世界 0");
        var parkedFolder = SaveSlotService.NewParkedFolderName(id, tag);
        if (Directory.Exists(Path.Combine(saveRoot, parkedFolder)))
            throw new InvalidOperationException("当前世界预留的停放目录已存在；请先检查 SaveGames。");
        return new SaveSlotRegistry { ActiveSlotId = id, NextSlotId = 1, Slots =
            [new SaveSlot { Id = id, Tag = tag, WorldGuid = active.WorldGuid, ParkedFolder = parkedFolder }] };
    }

    private static async Task ValidateStateAsync(string root, RegisteredServer server, CancellationToken cancellationToken)
    {
        var registry = await ReadRequiredAsync<SaveSlotRegistry>(Path.Combine(root, "SaveSlots.json"), cancellationToken);
        if (registry.Version != 2 || registry.Slots is null || registry.Slots.Count == 0 ||
            !registry.Slots.Any(slot => slot.Id == registry.ActiveSlotId) ||
            registry.Slots.Select(slot => slot.Id).Distinct().Count() != registry.Slots.Count ||
            registry.Slots.Select(slot => slot.WorldGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != registry.Slots.Count)
            throw new InvalidOperationException("SaveSlots.json 的存档登记或身份无效。");
        foreach (var slot in registry.Slots)
        {
            if (!Guid.TryParseExact(slot.WorldGuid, "N", out _) || string.IsNullOrWhiteSpace(slot.Tag))
                throw new InvalidOperationException($"存档 {slot.Id} 的 UID 或标签无效。");
            PathSafety.RequireChildName(slot.ParkedFolder);
            var worldRoot = slot.Id == registry.ActiveSlotId ?
                new ServerPaths(server.ServerRoot).ActivePath :
                Path.Combine(new ServerPaths(server.ServerRoot).SaveRoot, slot.ParkedFolder);
            if (!File.Exists(Path.Combine(worldRoot, slot.WorldGuid, "Level.sav")))
                throw new InvalidOperationException($"存档 {slot.Id} 的世界文件不存在。");
            var profile = await ReadRequiredAsync<WorldSettingsProfile>(Path.Combine(root, "WorldSettings", $"slot-{slot.Id:D3}.json"), cancellationToken);
            if (profile.SchemaVersion is < 1 or > WorldSettingsService.CurrentProfileSchema ||
                profile.SlotId != slot.Id || !profile.WorldGuid.Equals(slot.WorldGuid, StringComparison.OrdinalIgnoreCase) ||
                profile.Values is null)
                throw new InvalidOperationException($"存档 {slot.Id} 的世界设置身份无效。");
            if (profile.SchemaVersion == WorldSettingsService.CurrentProfileSchema)
            {
                var staged = new PalContext(AppPaths.ForExplicitRoots(root, root),
                    new ServerPaths(server.ServerRoot), new ServerStatePaths(root));
                var worldSettings = new WorldSettingsService(staged, new LoggingService(staged), new SafeFileService());
                await worldSettings.LoadProfileAsync(slot.Id, slot.WorldGuid, cancellationToken);
            }
        }
        var settings = await ReadRequiredAsync<ManagerSettings>(Path.Combine(root, "ManagerSettings.json"), cancellationToken);
        if (settings.Version is < 1 or > 2 || settings.IdleShutdownMinutes is < 1 or > 1440)
            throw new InvalidOperationException("ManagerSettings.json 无效。");
        var activity = await ReadRequiredAsync<PlayerActivityDocument>(Path.Combine(root, "PlayerActivity.json"), cancellationToken);
        if (activity.Version != 1 || activity.Worlds is null) throw new InvalidOperationException("PlayerActivity.json 无效。");
        var builds = await ReadRequiredAsync<BuildStateDocument>(Path.Combine(root, "WorldBuildState.json"), cancellationToken);
        if (builds.SchemaVersion != 1 || builds.Worlds is null) throw new InvalidOperationException("WorldBuildState.json 无效。");
        var markerPath = new ServerStatePaths(root).WorldSettingsMarkerPath;
        if (!File.Exists(markerPath)) throw new InvalidOperationException("缺少世界设置初始化记录。");
        using (var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken)))
            if (!marker.RootElement.TryGetProperty("schemaVersion", out var version) ||
                version.GetInt32() is < 1 or > WorldSettingsService.CurrentProfileSchema)
                throw new InvalidOperationException("世界设置初始化记录无效。");
        if (File.Exists(Path.Combine(root, "PendingOperation.json"))) throw new InvalidOperationException("存在未完成操作。");
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(Path.Combine(root, "Logs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"JSON 内容为空：{Path.GetRelativePath(root, path)}");
        }
    }

    private static async Task<T> ReadRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("缺少必需的管理状态文件。", path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"{Path.GetFileName(path)} 内容为空。");
    }
}

public sealed record DiscoveredWorld(string FolderName, string WorldGuid, bool IsActive);
