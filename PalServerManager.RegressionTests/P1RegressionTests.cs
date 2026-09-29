using System.Reflection;
using System.Runtime.CompilerServices;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;

internal static class P1RegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        Console.WriteLine("RUN P1 create-world rollback authority");
        await CreateWorldRollbackRestoresBothAuthoritiesAsync();
        Console.WriteLine("PASS P1 create-world rollback authority");

        Console.WriteLine("RUN P1 roster snapshot/dispose never wait on async gate");
        await PlayerRosterReadAndDisposeAreNonBlockingAsync();
        Console.WriteLine("PASS P1 roster snapshot/dispose never wait on async gate");
    }

    private static async Task CreateWorldRollbackRestoresBothAuthoritiesAsync()
    {
        foreach (var oldWorldAlreadyParked in new[] { false, true })
        {
            await InTempAsync(async root =>
            {
                var env = CreateEnvironment(root);
                const string oldUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
                const string newUid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
                var oldSlot = new SaveSlot
                {
                    Id = 0,
                    Tag = "old",
                    WorldGuid = oldUid,
                    ParkedFolder = SaveSlotService.NewParkedFolderName(0, "old")
                };
                var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots = [oldSlot] };
                CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, oldUid), "old-world");
                Directory.CreateDirectory(env.Context.ServerPaths.ConfigRoot);
                await File.WriteAllTextAsync(env.Context.ServerPaths.SettingsPath, "runtime-original");
                await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={oldUid}");
                await env.Slots.SaveAsync(registry);
                await WriteProfileAsync(env, oldSlot, oldUid);
                var originalBuildDocument = new BuildStateDocument();
                originalBuildDocument.Worlds[oldUid] = new WorldBuildState
                {
                    SlotId = 0,
                    WorldUid = oldUid,
                    LastSuccessfulBuild = "synthetic:old",
                    VerifiedUtc = DateTimeOffset.UtcNow
                };
                await env.Files.WriteJsonAsync(env.Context.StatePaths.BuildStatePath, originalBuildDocument);

                var originalRegistry = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
                var originalRuntime = await File.ReadAllBytesAsync(env.Context.ServerPaths.SettingsPath);
                var originalUser = await File.ReadAllBytesAsync(env.Context.ServerPaths.UserSettingsPath);
                var originalBuildState = await File.ReadAllBytesAsync(env.Context.StatePaths.BuildStatePath);
                var transactionOld = Path.Combine(env.Context.ServerPaths.SaveRoot, $".pal-new-old-{Guid.NewGuid():N}");
                var currentParked = Path.Combine(env.Context.ServerPaths.SaveRoot, oldSlot.ParkedFolder);

                Directory.Move(env.Context.ServerPaths.ActivePath, transactionOld);
                Directory.CreateDirectory(env.Context.ServerPaths.ActivePath);
                CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, newUid), "generated-world");
                if (oldWorldAlreadyParked) Directory.Move(transactionOld, currentParked);

                var newSlot = new SaveSlot
                {
                    Id = 1,
                    Tag = "new",
                    WorldGuid = newUid,
                    ParkedFolder = SaveSlotService.NewParkedFolderName(1, "new")
                };
                await WriteProfileAsync(env, newSlot, newUid);
                registry.Slots.Add(newSlot);
                registry.ActiveSlotId = 1;
                registry.NextSlotId = 2;
                await env.Slots.SaveAsync(registry);
                await env.Slots.SetWorldGuidAsync(newUid);
                await File.WriteAllTextAsync(env.Context.ServerPaths.SettingsPath, "runtime-mutated");
                var mutatedBuildDocument = new BuildStateDocument();
                mutatedBuildDocument.Worlds[oldUid] = originalBuildDocument.Worlds[oldUid];
                mutatedBuildDocument.Worlds[newUid] = new WorldBuildState
                {
                    SlotId = 1,
                    WorldUid = newUid,
                    LastSuccessfulBuild = "synthetic:new",
                    VerifiedUtc = DateTimeOffset.UtcNow
                };
                await env.Files.WriteJsonAsync(env.Context.StatePaths.BuildStatePath, mutatedBuildDocument, keepPrevious: true);

                var journal = new OperationJournalService(env.Context, env.Files);
                await journal.WriteAsync(new PendingOperation
                {
                    Type = "CreateAndStart",
                    FromSlot = 0,
                    FromWorldUid = oldUid,
                    ToSlot = 1,
                    ToWorldUid = newUid
                }, "SyntheticFailure");

                using var http = new HttpClient();
                var identity = new WorldIdentityAuditService(env.Context, env.Slots, env.WorldSettings);
                var manager = new ServerManagerService(
                    env.Context,
                    env.Processes,
                    new PalRestApiService(env.Context, http),
                    env.Slots,
                    env.WorldSettings,
                    new KeepAwakeService(env.Context, env.Processes, env.Log),
                    new BackupService(env.Context, env.Processes, env.Slots, env.WorldSettings, env.Files, env.Log),
                    identity,
                    new WorldOptionService(env.Slots, env.Log),
                    new PalServerBuildService(env.Context, env.Files),
                    journal,
                    env.Files,
                    new NoopPowerService(),
                    env.Log);

                var recovery = new List<Exception>();
                await manager.RecoverCreateAndStartAsync(
                    1, transactionOld, currentParked, originalRegistry, originalRuntime, originalUser, originalBuildState, recovery);

                if (recovery.Count != 0)
                    throw new AggregateException("Synthetic create-world recovery did not verify cleanly.", recovery);
                var restored = await env.Slots.LoadAsync();
                Equal(0, restored.ActiveSlotId);
                Equal(1, restored.Slots.Count);
                Equal(oldUid, restored.Slots.Single().WorldGuid);
                Equal("old-world", await File.ReadAllTextAsync(Path.Combine(env.Context.ServerPaths.ActivePath, oldUid, "Level.sav")));
                True(!Directory.Exists(currentParked), "old active world remained parked after recovery");
                True(!Directory.Exists(transactionOld), "temporary old-world authority remained after recovery");
                True(!File.Exists(env.Context.StatePaths.WorldProfilePath(1)), "failed new-world profile survived recovery");
                var restoredRegistryBytes = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
                var restoredRuntimeBytes = await File.ReadAllBytesAsync(env.Context.ServerPaths.SettingsPath);
                var restoredUserBytes = await File.ReadAllBytesAsync(env.Context.ServerPaths.UserSettingsPath);
                var restoredBuildStateBytes = await File.ReadAllBytesAsync(env.Context.StatePaths.BuildStatePath);
                True(originalRegistry.SequenceEqual(restoredRegistryBytes), "registry bytes were not restored");
                True(originalRuntime.SequenceEqual(restoredRuntimeBytes), "runtime INI bytes were not restored");
                True(originalUser.SequenceEqual(restoredUserBytes), "GameUserSettings.ini bytes were not restored");
                True(originalBuildState.SequenceEqual(restoredBuildStateBytes), "build-state bytes were not restored");
                await identity.AuditOrThrowAsync(restored, checkInterruptedOperation: false);
                True(File.Exists(env.Context.StatePaths.PendingOperationPath), "recovery helper cleared transaction authority before caller completion");
                journal.Complete();
            });
        }
    }

    private static async Task PlayerRosterReadAndDisposeAreNonBlockingAsync()
    {
        await InTempAsync(async root =>
        {
            var env = CreateEnvironment(root);
            const string uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var players = Path.Combine(env.Context.ServerPaths.ActivePath, uid, "Players");
            Directory.CreateDirectory(players);
            var roster = new PlayerRosterService(env.Context, env.Files);
            await roster.RefreshAsync(uid, players, [], PlayerPresenceMode.ServerStopped, DateTimeOffset.UtcNow);
            var gate = GetRosterGate(roster);
            await gate.WaitAsync();
            try
            {
                var read = Task.Run(() => roster.GetCurrentSnapshot(DateTimeOffset.UtcNow));
                var winner = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1)));
                True(ReferenceEquals(winner, read), "GetCurrentSnapshot blocked on the async refresh gate");
                await read;
            }
            finally
            {
                gate.Release();
                roster.Dispose();
            }

            var disposeRoster = new PlayerRosterService(env.Context, env.Files);
            var disposeGate = GetRosterGate(disposeRoster);
            await disposeGate.WaitAsync();
            try
            {
                var dispose = Task.Run(disposeRoster.Dispose);
                var winner = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(1)));
                True(ReferenceEquals(winner, dispose), "Dispose blocked on the async refresh gate");
                await dispose;
            }
            finally
            {
                disposeGate.Release();
            }
        });
    }

    private static SemaphoreSlim GetRosterGate(PlayerRosterService roster) =>
        (SemaphoreSlim)(typeof(PlayerRosterService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(roster)
            ?? throw new InvalidOperationException("PlayerRosterService gate field was not found."));

    private static TestEnvironment CreateEnvironment(string root)
    {
        var server = Path.Combine(root, "PalServer");
        var app = Path.Combine(root, "App");
        var state = Path.Combine(root, "State");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(state);
        File.WriteAllBytes(Path.Combine(server, "PalServer.exe"), [0]);
        var registered = RegisteredServer.Create("synthetic server", server);
        var context = PalContext.ForRegisteredServer(new AppPaths(app, state), registered);
        var files = new SafeFileService();
        var log = new LoggingService(context);
        var processes = new ServerProcessService(context, log);
        var slots = new SaveSlotService(context, processes, log, files);
        var worldSettings = new WorldSettingsService(context, log, files);
        return new TestEnvironment(context, files, log, processes, slots, worldSettings);
    }

    private static Task WriteProfileAsync(TestEnvironment env, SaveSlot slot, string uid)
    {
        var values = WorldSettingsService.ProfileDefinitions.ToDictionary(def => def.Key, def => def.Kind switch
        {
            WorldSettingKind.Boolean => "False",
            WorldSettingKind.Choice => def.Choices![0],
            WorldSettingKind.Integer => Math.Ceiling(def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorldSettingKind.Number => Math.Max(def.Minimum, def.Minimum == 0 ? 1 : def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => ""
        }, StringComparer.OrdinalIgnoreCase);
        return env.Files.WriteJsonAsync(env.Context.StatePaths.WorldProfilePath(slot.Id), new WorldSettingsProfile
        {
            SlotId = slot.Id,
            WorldGuid = uid,
            Tag = slot.Tag,
            Values = values
        });
    }

    private static void CreateWorld(string path, string level)
    {
        Directory.CreateDirectory(Path.Combine(path, "Players"));
        File.WriteAllText(Path.Combine(path, "Level.sav"), level);
        File.WriteAllText(Path.Combine(path, "LevelMeta.sav"), "synthetic-meta");
    }

    private static async Task InTempAsync(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "PalServerManager-P1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { await body(path); }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class NoopPowerService : IPowerService
    {
        public void ScheduleShutdown() { }
    }

    private sealed record TestEnvironment(
        PalContext Context,
        SafeFileService Files,
        LoggingService Log,
        ServerProcessService Processes,
        SaveSlotService Slots,
        WorldSettingsService WorldSettings);
}
