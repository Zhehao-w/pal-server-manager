using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("atomic JSON create/replace/stale/concurrent", AtomicJsonAsync),
    ("duplicate registered world UID hard block", DuplicateUidAsync),
    ("identity audit detects parked/active/profile mismatch", IdentityMismatchAsync),
    ("WorldOption detection and reversible disable", WorldOptionAsync),
    ("per-world build acceptance is independent", BuildStateAsync),
    ("pre-update manifest, DPS, and optional settings restore", SnapshotManifestAsync),
    ("DPS excluded from roster count and preserved by rollback", DpsRollbackAsync),
    ("standalone app/state/server path separation", SeparatedPathsAsync),
    ("explicit state roots remain isolated and diagnosable", StateRootIsolationAsync),
    ("verified snapshot failures never replace live world", SnapshotFailureAsync),
    ("restore faults recover world or retain journal", RestoreFailureAsync),
    ("logical cross-volume restore uses only same-volume renames", LogicalCrossVolumeAsync),
    ("server registry and broken-path relocation", RegistryFlowAsync),
    ("existing PalServer worlds attach without modifying saves", ExistingWorldAttachAsync),
    ("incomplete active world and invalid Manager state fail closed", AttachSafetyAsync),
    ("new world waits for its first disk save and rejects wrong UID", NewWorldSaveWaitAsync),
    ("zero-player world can create protection and restore", ZeroPlayerRestoreAsync)
    ,("inactive world deletion snapshots and cleans metadata", DeleteWorldSuccessAsync)
    ,("world deletion refuses unsafe state and preserves originals", DeleteWorldRefusalsAsync)
    ,("world deletion failures restore data and metadata", DeleteWorldFailureAsync)
    ,("fresh PC attaches manually copied worlds without touching saves", FreshPcAttachAsync)
    ,("world discovery reports managed, unmanaged, duplicate and incomplete states", WorldDiscoveryAsync)
    ,("rescan is read-only and profiles reconcile without repair", ReadOnlyRescanAsync)
    ,("canonical copy import uses monotonic IDs and selected settings", ExistingWorldImportAsync)
    ,("canonical parked world is adopted in place after read-only discovery", CanonicalWorldAdoptionAsync)
    ,("canonical adoption restores WorldOption after post-rename failure", CanonicalAdoptionWorldOptionRecoveryAsync)
    ,("import refuses unsafe state and rolls back metadata failures", WorldImportRefusalsAsync)
    ,("first-run setup attaches atomically from the registry perspective", FirstRunSetupAsync)
    ,("registration restores the registry when state directory creation fails", RegistrationBoundaryRollbackAsync)
};
var selectedTests = args.Length == 0 ? tests : tests.Where(test => args.Any(filter =>
    test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
var failures = new List<string>();
foreach (var test in selectedTests)
{
    Console.WriteLine($"RUN {test.Name}");
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures.Add($"FAIL {test.Name}: {error}"); Console.WriteLine(failures[^1]); }
}
if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} of {selectedTests.Length} reliability regression groups failed.");
    Environment.ExitCode = 1;
    return;
}
Console.WriteLine($"All {selectedTests.Length} reliability regression groups passed.");

static async Task AtomicJsonAsync()
{
    await InTempAsync(async root =>
    {
        var service = new SafeFileService(); var path = Path.Combine(root, "state.json");
        await service.WriteJsonAsync(path, new { value = 1 });
        Equal(1, JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("value").GetInt32());
        await File.WriteAllTextAsync(path, "{broken");
        await service.WriteJsonAsync(path, new { value = 2 }, keepPrevious: true);
        Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("value").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(root, ".state.json.tmp-1-stale"), "stale");
        await Task.WhenAll(Enumerable.Range(3, 25).Select(value => service.WriteJsonAsync(path, new { value })));
        _ = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        True(!Directory.EnumerateFiles(root, ".state.json.tmp-*").Any(), "stale temp remains");
    });
}

static async Task DuplicateUidAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var registry = Registry(uid, uid);
        await env.Files.WriteJsonAsync(env.Context.StatePaths.RegistryPath, registry);
        await ThrowsAsync<InvalidOperationException>(() => env.Slots.LoadAsync());
    });
}

static async Task FirstRunSetupAsync()
{
    await InTempAsync(async root =>
    {
        var app = AppPaths.ForExplicitRoots(Path.Combine(root, "App"), Path.Combine(root, "State"));
        var files = new SafeFileService();
        var registry = new ServerRegistryService(app, files);
        var state = new ServerStateService(app, files);
        var setup = new FirstRunServerSetupService(registry, state);
        var serverRoot = Path.Combine(root, "PalServer");
        FakeServer(serverRoot);

        await ThrowsAsync<InvalidOperationException>(() => setup.RegisterAndAttachAsync(
            Path.Combine(serverRoot, "PalServer.exe"), "synthetic"));
        Equal(0, (await registry.LoadAsync()).Servers.Count);

        var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        CreateWorld(Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0", uid), "existing");
        await WriteSyntheticIniAsync(serverRoot);
        var registered = await setup.RegisterAndAttachAsync(Path.Combine(serverRoot, "PalServer.exe"), "synthetic");
        Equal(registered.Id, (await registry.LoadAsync()).Servers.Single().Id);
        Equal(ServerStateKind.Ready, (await state.AssessAsync(registered)).Kind);
    });
}

static async Task RegistrationBoundaryRollbackAsync()
{
    await InTempAsync(async root =>
    {
        var app = AppPaths.ForExplicitRoots(Path.Combine(root, "App"), Path.Combine(root, "State"));
        var files = new SafeFileService();
        var registry = new ServerRegistryService(app, files);
        var existingRoot = Path.Combine(root, "ExistingPalServer");
        var newRoot = Path.Combine(root, "NewPalServer");
        FakeServer(existingRoot); FakeServer(newRoot);
        var existing = RegisteredServer.Create("existing", existingRoot);
        await files.WriteJsonAsync(app.ServerRegistryPath, new ServerRegistryDocument
        {
            SelectedServerId = existing.Id,
            Servers = [existing]
        });
        await File.WriteAllTextAsync(app.ServersStateRoot, "synthetic obstruction");

        await ThrowsAsync<IOException>(() => registry.RegisterFromExeAsync(
            Path.Combine(newRoot, "PalServer.exe"), "new"));
        var restored = await registry.LoadAsync();
        Equal(1, restored.Servers.Count);
        Equal(existing.Id, restored.Servers.Single().Id);
        Equal(existing.Id, restored.SelectedServerId!);

        File.Delete(app.ServersStateRoot);
        var registered = await registry.RegisterFromExeAsync(Path.Combine(newRoot, "PalServer.exe"), "new");
        var retried = await registry.LoadAsync();
        Equal(2, retried.Servers.Count);
        Equal(registered.Id, retried.SelectedServerId!);
    });
}

static async Task IdentityMismatchAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        var uid0 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; var uid1 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var registry = Registry(uid0, uid1);
        CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, uid0), "one");
        var parked = Path.Combine(env.Context.ServerPaths.SaveRoot, registry.Slots[1].ParkedFolder);
        CreateWorld(Path.Combine(parked, uid1), "two"); CreateWorld(Path.Combine(parked, uid0), "duplicate");
        Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!);
        await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={uid1}");
        await env.Files.WriteJsonAsync(env.Context.StatePaths.RegistryPath, registry);
        await WriteProfileAsync(env, registry.Slots[0], uid1);
        await WriteProfileAsync(env, registry.Slots[1], uid1);
        var audit = await new WorldIdentityAuditService(env.Context, env.Slots, env.WorldSettings).AuditAsync(registry);
        True(audit.Issues.Any(issue => issue.Code == "DirectoryUidMismatch" || issue.Code == "AmbiguousWorldDirectory"), "parked mismatch not detected");
        True(audit.Issues.Any(issue => issue.Code == "ProfileIdentity"), "profile mismatch not detected");
        True(audit.Issues.Any(issue => issue.Code == "DedicatedServerNameMismatch"), "active runtime mismatch not detected");
    });
}

static async Task DpsRollbackAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root); var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots = [new SaveSlot { Id = 0, Tag = "test", WorldGuid = uid, ParkedFolder = "0 - Slot 000 - test" }] };
        var world = Path.Combine(env.Context.ServerPaths.ActivePath, uid); CreateWorld(world, "live");
        await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1.sav"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1_dps.sav"), [2, 3, 4]);
        Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!);
        await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={uid}");
        await env.Files.WriteJsonAsync(env.Context.StatePaths.RegistryPath, registry); await WriteProfileAsync(env, registry.Slots[0], uid);
        var native = Path.Combine(world, "backup", "world", "2026.09.15-12.00.00"); CreateWorld(native, "backup");
        await File.WriteAllBytesAsync(Path.Combine(native, "Players", "P1.sav"), [9]);
        var expectedDps = new byte[] { 7, 8, 9, 10 }; await File.WriteAllBytesAsync(Path.Combine(native, "Players", "P1_dps.sav"), expectedDps);
        var backupService = new BackupService(env.Context, env.Processes, env.Slots, env.WorldSettings, env.Files, env.Log);
        var displays = await env.Slots.GetDisplaysAsync(registry); Equal("1", displays[0].Players);
        var entry = (await backupService.ListAsync(registry)).Single(item => item.Kind == BackupKind.Native);
        Equal(1, entry.PlayerCount);
        await backupService.RestoreAsync(registry, entry);
        var restoredDps = await File.ReadAllBytesAsync(Path.Combine(world, "Players", "P1_dps.sav"));
        True(expectedDps.SequenceEqual(restoredDps), "DPS bytes changed during rollback");
    });
}

static async Task WorldOptionAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root); var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, Slots = [new SaveSlot { Id = 0, Tag = "test", WorldGuid = uid, ParkedFolder = "0 - Slot 000 - test" }] };
        var world = Path.Combine(env.Context.ServerPaths.ActivePath, uid); CreateWorld(world, "live");
        var one = Path.Combine(world, "WorldOption.sav"); var many = Path.Combine(world, "WorldOptions.sav");
        await File.WriteAllTextAsync(one, "one"); await File.WriteAllTextAsync(many, "many");
        var service = new WorldOptionService(env.Slots, env.Log); var found = service.Detect(registry, 0);
        Equal(2, found.Count); var disabled = await service.BackupAndDisableAsync(found[0]);
        True(!File.Exists(found[0].FilePath) && File.Exists(disabled), "WorldOption disable was not reversible");
    });
}

static async Task BuildStateAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root); var service = new PalServerBuildService(env.Context, env.Files); var build = await service.GetCurrentBuildAsync();
        var a = new SaveSlot { Id = 0, WorldGuid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        var b = new SaveSlot { Id = 1, WorldGuid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" };
        True(await service.NeedsProtectionAsync(a.WorldGuid, build), "new world should need protection");
        await service.MarkSuccessfulAsync(a, build);
        True(!await service.NeedsProtectionAsync(a.WorldGuid, build), "accepted world repeated prompt");
        True(await service.NeedsProtectionAsync(b.WorldGuid, build), "one world incorrectly acknowledged another world");
        True(await service.NeedsProtectionAsync(a.WorldGuid, build + "-new"), "new build was incorrectly accepted");
    });
}

static async Task SnapshotManifestAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root); var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var slot = new SaveSlot { Id = 0, Tag = "snapshot", WorldGuid = uid, ParkedFolder = "0 - Slot 000 - snapshot" };
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots = [slot] };
        var world = Path.Combine(env.Context.ServerPaths.ActivePath, uid); CreateWorld(world, "original");
        await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1.sav"), [1]);
        var dps = new byte[] { 4, 5, 6 }; await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1_dps.sav"), dps);
        Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!); await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={uid}");
        await env.Files.WriteJsonAsync(env.Context.StatePaths.RegistryPath, registry); await WriteProfileAsync(env, slot, uid);
        var backupService = new BackupService(env.Context, env.Processes, env.Slots, env.WorldSettings, env.Files, env.Log);
        var snapshot = await backupService.CreatePreUpdateSnapshotAsync(registry, 0, "steam:101");
        True(snapshot.HasManagerManifest && File.Exists(Path.Combine(snapshot.Path, "manager-manifest.json")), "manifest missing");
        var snapshotDps = await File.ReadAllBytesAsync(Path.Combine(snapshot.Path, "Players", "P1_dps.sav"));
        True(dps.SequenceEqual(snapshotDps), "pre-update DPS bytes changed");
        var profile = await env.WorldSettings.LoadProfileAsync(0, uid); profile.Values["DayTimeSpeedRate"] = "2";
        await env.Files.WriteJsonAsync(env.WorldSettings.GetProfilePath(0), profile, keepPrevious: true);
        await File.WriteAllTextAsync(Path.Combine(world, "Level.sav"), "mutated");
        await backupService.RestoreAsync(registry, snapshot with { RestoreWorldSettings = true });
        var restored = await env.WorldSettings.LoadProfileAsync(0, uid);
        Equal("1", restored.Values["DayTimeSpeedRate"]);
    });
}

static async Task SeparatedPathsAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root); var c = env.Context;
        True(c.AppPaths.InstallRoot != c.StatePaths.StateRoot && c.StatePaths.StateRoot != c.ServerPaths.ServerRoot, "path domains overlap");
        True(!c.StatePaths.StateRoot.StartsWith(c.ServerPaths.ServerRoot, StringComparison.OrdinalIgnoreCase), "state is inside PalServer");
        True(c.AppPaths.KeepAwakeExecutable == Path.Combine(root, "App", "Helpers", "PalServer-KeepAwake.exe"), "helper is not install-relative");
        True(c.ServerPaths.BuiltInBackupRoot(Path.Combine(c.ServerPaths.ActivePath, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
            .StartsWith(c.ServerPaths.SaveRoot, StringComparison.OrdinalIgnoreCase), "native backup escaped server root");
        var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1,
            Slots = [new SaveSlot { Id = 0, Tag = "fake", WorldGuid = uid, ParkedFolder = "0 - Slot 000 - fake" }] };
        CreateWorld(Path.Combine(c.ServerPaths.ActivePath, uid), "fake world");
        await ThrowsAsync<InvalidOperationException>(() => env.Slots.LoadAsync());
        True(!File.Exists(c.StatePaths.RegistryPath), "empty standalone state was silently imported");
        await env.Files.WriteJsonAsync(c.StatePaths.RegistryPath, registry);
        Equal(0, (await env.Slots.LoadAsync()).ActiveSlotId);
        await new SettingsService(c, env.Files).SaveAsync(new ManagerSettings());
        True(File.Exists(c.StatePaths.ManagerSettingsPath), "ManagerSettings not written to state root");
        await WriteProfileAsync(env, registry.Slots[0], uid);
        True(File.Exists(env.WorldSettings.GetProfilePath(0)), "profile not written to state root");
        var journal = new OperationJournalService(c, env.Files);
        await journal.WriteAsync(new PendingOperation { Type = "Synthetic" }, "Prepared");
        True(File.Exists(c.StatePaths.PendingOperationPath), "journal not written to state root");
        journal.Complete();
        await new PalServerBuildService(c, env.Files).MarkSuccessfulAsync(registry.Slots[0], "fake:1");
        True(File.Exists(c.StatePaths.BuildStatePath), "build state not written to state root");
        await env.Log.WriteAsync("synthetic test");
        True(File.Exists(c.StatePaths.LogPath), "log not written to state root");
        Directory.CreateDirectory(c.ServerPaths.ConfigRoot);
        await File.WriteAllTextAsync(c.ServerPaths.SettingsPath, "OptionSettings=(AdminPassword=\"fake\")");
        await File.WriteAllTextAsync(c.ServerPaths.UserSettingsPath, $"DedicatedServerName={uid}");
        True(File.Exists(c.ServerPaths.SettingsPath) && File.Exists(c.ServerPaths.UserSettingsPath), "game config missing");
        using var roster = new PlayerRosterService(c, env.Files);
        await roster.RefreshAsync(uid, Path.Combine(c.ServerPaths.ActivePath, uid, "Players"), [],
            PlayerPresenceMode.ServerStopped, DateTimeOffset.UtcNow);
        True(File.Exists(c.StatePaths.PlayerActivityPath), "player activity not written to state root");
        True(!File.Exists(Path.Combine(c.AppPaths.InstallRoot, "SaveSlots.json")), "state leaked into install root");
    });
}

static async Task StateRootIsolationAsync()
{
    await InTempAsync(async root =>
    {
        var install = Path.Combine(root, "App");
        var serverRoot = Path.Combine(root, "PalServer");
        FakeServer(serverRoot);
        var appA = AppPaths.ForExplicitRoots(install, Path.Combine(root, "StateA"));
        var appB = AppPaths.ForExplicitRoots(install, Path.Combine(root, "StateB"));
        var files = new SafeFileService();
        var registryA = new ServerRegistryService(appA, files);
        var registryB = new ServerRegistryService(appB, files);
        var serverA = await registryA.RegisterFromExeAsync(Path.Combine(serverRoot, "PalServer.exe"), "A");
        var serverB = await registryB.RegisterFromExeAsync(Path.Combine(serverRoot, "PalServer.exe"), "B");
        True(serverA.Id != serverB.Id, "independent state roots reused a server ID");
        Equal(serverA.Id, (await registryA.LoadAsync()).SelectedServerId!);
        Equal(serverB.Id, (await registryB.LoadAsync()).SelectedServerId!);
        True(!appA.ServerRegistryPath.Equals(appB.ServerRegistryPath, StringComparison.OrdinalIgnoreCase), "registry paths overlapped");
        var aState = ServerStatePaths.ForRegisteredServer(appA, serverA);
        var bState = ServerStatePaths.ForRegisteredServer(appB, serverB);
        await files.WriteJsonAsync(aState.RegistryPath, new SaveSlotRegistry());
        True(!File.Exists(bState.RegistryPath), "state A leaked into state B");
        var diagnostic = StartupDiagnosticsService.Describe(appA, serverA);
        True(diagnostic.Contains(appA.LocalStateRoot, StringComparison.Ordinal) &&
             diagnostic.Contains(appA.ServerRegistryPath, StringComparison.Ordinal) &&
             diagnostic.Contains(aState.StateRoot, StringComparison.Ordinal) &&
             diagnostic.Contains(serverA.Id, StringComparison.Ordinal), "startup diagnostics omitted state identity");
        True(!diagnostic.Contains(appB.LocalStateRoot, StringComparison.Ordinal), "startup diagnostics crossed state roots");
    });
}

static async Task SnapshotFailureAsync()
{
    foreach (var fault in new[] { BackupCheckpoint.BeforeProtection, BackupCheckpoint.DuringCopy,
                 BackupCheckpoint.AfterCopyBeforeVerify, BackupCheckpoint.BeforePublish })
    {
        await InTempAsync(async root =>
        {
            var fixture = await CreateRestoreFixtureAsync(root);
            var backup = FaultyBackup(fixture, (point, path) =>
            {
                if (point != fault) return;
                if (point == BackupCheckpoint.AfterCopyBeforeVerify) File.Delete(Path.Combine(path, "Level.sav"));
                else throw new IOException("synthetic snapshot fault");
            });
            await ThrowsAsync<Exception>(() => backup.RestoreAsync(fixture.Registry, fixture.Native));
            Equal("live", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
            True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "pre-replace failure left a journal");
            True(!(await backup.ListAsync(fixture.Registry)).Any(x => x.Kind == BackupKind.Protection), "incomplete snapshot was published");
        });
    }
    await InTempAsync(async root =>
    {
        var fixture = await CreateRestoreFixtureAsync(root);
        var backup = FaultyBackup(fixture, null, (_, _) => false);
        await ThrowsAsync<IOException>(() => backup.RestoreAsync(fixture.Registry, fixture.Native));
        Equal("live", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
    });
    await InTempAsync(async root =>
    {
        var fixture = await CreateRestoreFixtureAsync(root);
        var backup = FaultyBackup(fixture, (point, path) =>
        {
            if (point == BackupCheckpoint.AfterCopyBeforeVerify) File.WriteAllText(Path.Combine(path, "Level.sav"), "evil");
        });
        await ThrowsAsync<InvalidOperationException>(() => backup.RestoreAsync(fixture.Registry, fixture.Native));
        Equal("live", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
    });
}

static async Task RestoreFailureAsync()
{
    foreach (var fault in new[] { BackupCheckpoint.BeforeActiveReplace, BackupCheckpoint.DuringRestore })
    {
        await InTempAsync(async root =>
        {
            var fixture = await CreateRestoreFixtureAsync(root); var moves = 0;
            var backup = FaultyBackup(fixture, (point, _) =>
            {
                if (point == fault && (point != BackupCheckpoint.DuringRestore || ++moves == 3))
                    throw new IOException("synthetic restore fault");
            });
            await ThrowsAsync<InvalidOperationException>(() => backup.RestoreAsync(fixture.Registry, fixture.Native));
            Equal("live", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
            True((await File.ReadAllBytesAsync(Path.Combine(fixture.World, "Players", "P1_dps.sav"))).SequenceEqual(new byte[] { 1, 2, 3 }), "live DPS changed after recovery");
            True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "recovered restore retained journal");
            var protection = (await backup.ListAsync(fixture.Registry)).Single(x => x.Kind == BackupKind.Protection);
            True(File.Exists(Path.Combine(protection.Path, "manager-manifest.json")), "protection manifest missing");
        });
    }
    await InTempAsync(async root =>
    {
        var fixture = await CreateRestoreFixtureAsync(root);
        var backup = FaultyBackup(fixture, (point, _) =>
        {
            if (point is BackupCheckpoint.DuringRestore or BackupCheckpoint.DuringRecovery) throw new IOException("synthetic unrecoverable fault");
        });
        await ThrowsAsync<AggregateException>(() => backup.RestoreAsync(fixture.Registry, fixture.Native));
        True(File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "unrecovered failure lost journal");
        True(Directory.EnumerateDirectories(fixture.Env.Context.ServerPaths.SaveRoot, ".pal-restore-old-*").Any(), "old payload evidence missing");
    });
}

static async Task LogicalCrossVolumeAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateRestoreFixtureAsync(root);
        var stateRoot = Path.GetFullPath(fixture.Env.Context.StatePaths.StateRoot);
        var serverRoot = Path.GetFullPath(fixture.Env.Context.ServerPaths.ServerRoot);
        string LogicalVolume(string path)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(stateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "manager-volume";
            if (full.StartsWith(serverRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "palworld-volume";
            throw new InvalidOperationException("Unexpected path outside fake roots: " + full);
        }
        var service = new BackupService(fixture.Env.Context, fixture.Env.Processes, fixture.Env.Slots,
            fixture.Env.WorldSettings, fixture.Env.Files, fixture.Env.Log,
            capacityProbe: (_, _) => true, volumeIdentity: LogicalVolume);
        var protection = await service.RestoreAsync(fixture.Registry, fixture.Native);
        Equal("back", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
        True((await File.ReadAllBytesAsync(Path.Combine(fixture.World, "Players", "P1_dps.sav")))
            .SequenceEqual(new byte[] { 7, 8, 9 }), "DPS did not survive cross-volume restore");
        True(protection.StartsWith(stateRoot, StringComparison.OrdinalIgnoreCase), "protection snapshot not in Manager state");
        True(File.Exists(Path.Combine(protection, "manager-manifest.json")), "cross-volume manifest missing");
        True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "successful restore retained journal");
    });
}

static BackupService FaultyBackup(RestoreFixture f, Action<BackupCheckpoint, string>? fault,
    Func<string, long, bool>? capacity = null) =>
    new(f.Env.Context, f.Env.Processes, f.Env.Slots, f.Env.WorldSettings, f.Env.Files, f.Env.Log, fault, capacity);

static async Task<RestoreFixture> CreateRestoreFixtureAsync(string root)
{
    var env = CreateEnvironment(root); var uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    var slot = new SaveSlot { Id = 0, Tag = "synthetic", WorldGuid = uid, ParkedFolder = "0 - Slot 000 - synthetic" };
    var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots = [slot] };
    var world = Path.Combine(env.Context.ServerPaths.ActivePath, uid); CreateWorld(world, "live");
    await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1.sav"), [4]);
    await File.WriteAllBytesAsync(Path.Combine(world, "Players", "P1_dps.sav"), [1, 2, 3]);
    await env.Files.WriteJsonAsync(env.Context.StatePaths.RegistryPath, registry);
    await WriteProfileAsync(env, slot, uid);
    var native = Path.Combine(env.Context.ServerPaths.BuiltInBackupRoot(world), "2026.09.15-12.00.00");
    CreateWorld(native, "back");
    await File.WriteAllBytesAsync(Path.Combine(native, "Players", "P1.sav"), [9]);
    await File.WriteAllBytesAsync(Path.Combine(native, "Players", "P1_dps.sav"), [7, 8, 9]);
    var service = new BackupService(env.Context, env.Processes, env.Slots, env.WorldSettings, env.Files, env.Log);
    var entry = (await service.ListAsync(registry)).Single(x => x.Kind == BackupKind.Native);
    return new RestoreFixture(env, registry, world, entry);
}

static async Task RegistryFlowAsync()
{
    await InTempAsync(async root =>
    {
        var app = new AppPaths(Path.Combine(root, "App"), Path.Combine(root, "LocalState"));
        var files = new SafeFileService(); var registry = new ServerRegistryService(app, files);
        Equal(0, (await registry.LoadAsync()).Servers.Count);
        var firstRoot = Path.Combine(root, "One"); FakeServer(firstRoot);
        var first = await registry.RegisterFromExeAsync(Path.Combine(firstRoot, "PalServer.exe"), "Office server");
        True(Directory.Exists(ServerStatePaths.ForRegisteredServer(app, first).StateRoot), "per-server state root not created");
        Equal(first.Id, (await registry.LoadAsync()).SelectedServerId!);
        var secondRoot = Path.Combine(root, "Two"); FakeServer(secondRoot);
        var second = await registry.RegisterFromExeAsync(Path.Combine(secondRoot, "PalServer.exe"), "Backup server");
        Equal(2, (await registry.LoadAsync()).Servers.Count);
        True(first.Id != second.Id, "server ID reused");
        await ThrowsAsync<InvalidOperationException>(() => registry.RegisterFromExeAsync(Path.Combine(secondRoot, "PalServer.exe"), "duplicate"));
        File.Delete(Path.Combine(firstRoot, "PalServer.exe"));
        var state = new ServerStateService(app, files);
        Equal(ServerStateKind.InvalidServerRoot, (await state.AssessAsync(first)).Kind);
        var relocatedRoot = Path.Combine(root, "Relocated"); FakeServer(relocatedRoot);
        var relocated = await registry.RelocateAsync(first.Id, Path.Combine(relocatedRoot, "PalServer.exe"));
        Equal(first.Id, relocated.Id);
        Equal(ServerStateKind.NeedsInitialization, (await state.AssessAsync(relocated)).Kind);
        await registry.RemoveRegistrationAsync(first.Id);
        True(Directory.Exists(ServerStatePaths.ForRegisteredServer(app, first).StateRoot), "removing registration deleted state");
    });
}

static async Task NewWorldSaveWaitAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        const string expected = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var world = Path.Combine(env.Context.ServerPaths.ActivePath, expected);
        var writer = Task.Run(async () =>
        {
            await Task.Delay(300);
            Directory.CreateDirectory(world);
            await File.WriteAllBytesAsync(Path.Combine(world, "Level.sav"), [1]);
            await Task.Delay(300);
            await File.WriteAllBytesAsync(Path.Combine(world, "LevelMeta.sav"), [2]);
        });
        await env.Slots.WaitForActiveWorldSaveAsync(expected, TimeSpan.FromSeconds(3));
        await writer;
        True(File.Exists(Path.Combine(world, "LevelMeta.sav")), "wait returned before the complete initial save");
    });
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"), "wrong");
        await ThrowsAsync<InvalidOperationException>(() =>
            env.Slots.WaitForActiveWorldSaveAsync("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", TimeSpan.FromSeconds(2)));
    });
}

static async Task ZeroPlayerRestoreAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateRestoreFixtureAsync(root);
        Directory.Delete(Path.Combine(fixture.World, "Players"), true);
        Directory.Delete(Path.Combine(fixture.Native.Path, "Players"), true);
        Directory.CreateDirectory(Path.Combine(fixture.Native.Path, "Players"));
        var backups = new BackupService(fixture.Env.Context, fixture.Env.Processes, fixture.Env.Slots,
            fixture.Env.WorldSettings, fixture.Env.Files, fixture.Env.Log);
        var protection = await backups.RestoreAsync(fixture.Registry, fixture.Native);
        Equal("back", await File.ReadAllTextAsync(Path.Combine(fixture.World, "Level.sav")));
        True(Directory.Exists(Path.Combine(protection, "Players")), "empty player folder missing from protection snapshot");
        Equal("live", await File.ReadAllTextAsync(Path.Combine(protection, "Level.sav")));
        True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "restore journal remains");
    });
}

static async Task ExistingWorldAttachAsync()
{
    await InTempAsync(async root =>
    {
        var serverRoot = Path.Combine(root, "PalServer"); FakeServer(serverRoot);
        var app = AppPaths.ForExplicitRoots(Path.Combine(root, "App"), Path.Combine(root, "State"));
        var files = new SafeFileService();
        var server = await new ServerRegistryService(app, files).RegisterFromExeAsync(Path.Combine(serverRoot, "PalServer.exe"), "synthetic");
        var service = new ServerStateService(app, files);
        const string activeUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string parkedUid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var active = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0", activeUid);
        var parked = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0 - Slot 002 - Other", parkedUid);
        CreateWorld(active, "active-save"); CreateWorld(parked, "parked-save");
        await WriteSyntheticIniAsync(serverRoot);
        Equal(ServerStateKind.NeedsInitialization, (await service.AssessAsync(server)).Kind);
        Equal(1, service.DiscoverWorlds(server).Count);
        await service.AttachAsync(server);
        Equal(ServerStateKind.Ready, (await service.AssessAsync(server)).Kind);
        var state = ServerStatePaths.ForRegisteredServer(app, server);
        var registry = JsonSerializer.Deserialize<SaveSlotRegistry>(await File.ReadAllTextAsync(state.RegistryPath))!;
        Equal(1, registry.Slots.Count);
        Equal(0, registry.ActiveSlotId);
        Equal(SaveSlotService.NewParkedFolderName(0, "世界 0"), registry.Slots.Single().ParkedFolder);
        True(File.Exists(state.WorldProfilePath(0)), "active world profile missing");
        Equal("active-save", await File.ReadAllTextAsync(Path.Combine(active, "Level.sav")));
        Equal("parked-save", await File.ReadAllTextAsync(Path.Combine(parked, "Level.sav")));
        var discovered = await new WorldDiscoveryService(new ServerPaths(serverRoot), state).ScanAsync(registry);
        Equal(WorldDiscoveryStatus.Importable, discovered.Worlds.Single(world => world.WorldUid == parkedUid).Status);
        True(!Directory.Exists(Path.Combine(serverRoot, "Pal", "Saved", "AdminScripts")), "attach created a server-side Manager directory");

        // Previously activated state remains readable without its former source directory.
        await files.WriteJsonAsync(service.MarkerPath(server), new StateActivationRecord { ServerId = server.Id, Mode = "Migrated" });
        Equal(ServerStateKind.Ready, (await service.AssessAsync(server)).Kind);
    });
}

static async Task AttachSafetyAsync()
{
    await InTempAsync(async root =>
    {
        var serverRoot = Path.Combine(root, "PalServer"); FakeServer(serverRoot);
        var app = AppPaths.ForExplicitRoots(Path.Combine(root, "App"), Path.Combine(root, "State"));
        var files = new SafeFileService();
        var server = await new ServerRegistryService(app, files).RegisterFromExeAsync(Path.Combine(serverRoot, "PalServer.exe"), "synthetic");
        var service = new ServerStateService(app, files);
        await ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(server));
        const string uid = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        CreateWorld(Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0", uid), "active");
        var extra = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0 - Slot 001 - Copy");
        CreateWorld(Path.Combine(extra, uid), "copy");
        await WriteSyntheticIniAsync(serverRoot);
        var activeMeta = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0", uid, "LevelMeta.sav");
        File.Delete(activeMeta);
        await ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(server));
        var state = ServerStatePaths.ForRegisteredServer(app, server).StateRoot;
        True(!File.Exists(Path.Combine(state, "StateActivation.json")), "incomplete active world was activated");
        await File.WriteAllTextAsync(activeMeta, "synthetic-meta");
        // An extra disk folder is not silently adopted or made authoritative on first attach.
        Equal(1, service.DiscoverWorlds(server).Count);
        Directory.Delete(extra, true);
        await File.WriteAllTextAsync(Path.Combine(state, "PendingOperation.json"), "{}");
        Equal(ServerStateKind.Interrupted, (await service.AssessAsync(server)).Kind);
        await ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(server));
        File.Delete(Path.Combine(state, "PendingOperation.json"));
        await File.WriteAllTextAsync(Path.Combine(state, "unrecognized.json"), "{}");
        Equal(ServerStateKind.InvalidState, (await service.AssessAsync(server)).Kind);
        await ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(server));
        True(!File.Exists(Path.Combine(state, "StateActivation.json")), "untrusted state was activated");
    });
}

static async Task DeleteWorldSuccessAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        var beforeActive = await File.ReadAllBytesAsync(Path.Combine(fixture.Env.Context.ServerPaths.ActivePath, fixture.ActiveUid, "Level.sav"));
        var service = CreateDeletion(fixture);
        var snapshot = await service.DeleteAsync(1);
        True(Directory.Exists(snapshot), "protective snapshot missing");
        True(File.Exists(Path.Combine(snapshot, "manager-delete-manifest.json")), "snapshot manifest missing");
        True(File.Exists(Path.Combine(snapshot, "parked", fixture.ParkedUid, "backup", "world", "native.sav")), "built-in backup omitted from delete snapshot");
        True(File.Exists(Path.Combine(snapshot, "parked", fixture.ParkedUid, "WorldOption.sav")), "WorldOption omitted from delete snapshot");
        True(!Directory.Exists(fixture.ParkedPath), "parked world remains");
        var registry = await fixture.Env.Slots.LoadAsync();
        Equal(1, registry.Slots.Count); Equal(fixture.ActiveUid, registry.Slots[0].WorldGuid);
        True(!File.Exists(fixture.Env.Context.StatePaths.WorldProfilePath(1)), "deleted profile remains");
        True(!File.Exists(fixture.Env.Context.StatePaths.WorldProfilePath(1) + ".previous"), "deleted previous profile remains");
        True(!(await ReadBuildAsync(fixture.Env)).Worlds.ContainsKey(fixture.ParkedUid), "deleted build state remains");
        True(!(await ReadActivityAsync(fixture.Env)).Worlds.ContainsKey(fixture.ParkedUid), "deleted activity state remains");
        True((await ReadBuildAsync(fixture.Env)).Worlds.ContainsKey(fixture.ActiveUid), "retained build state lost");
        True((await ReadActivityAsync(fixture.Env)).Worlds.ContainsKey(fixture.ActiveUid), "retained activity state lost");
        var afterActive = await File.ReadAllBytesAsync(Path.Combine(fixture.Env.Context.ServerPaths.ActivePath, fixture.ActiveUid, "Level.sav"));
        True(beforeActive.SequenceEqual(afterActive), "active world changed");
        True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "successful delete retained journal");
        await new WorldIdentityAuditService(fixture.Env.Context, fixture.Env.Slots, fixture.Env.WorldSettings).AuditOrThrowAsync(registry);
    });
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        var noncanonical = Path.Combine(fixture.Env.Context.ServerPaths.SaveRoot, "legitimate-older-folder");
        Directory.Move(fixture.ParkedPath, noncanonical);
        var registry = await fixture.Env.Slots.LoadAsync();
        registry.Slots.Single(slot => slot.Id == 1).ParkedFolder = "legitimate-older-folder";
        await fixture.Env.Slots.SaveAsync(registry);
        var snapshot = await CreateDeletion(fixture).DeleteAsync(1);
        True(Directory.Exists(snapshot) && !Directory.Exists(noncanonical), "noncanonical registered world was not safely deleted");
        Equal(1, (await fixture.Env.Slots.LoadAsync()).Slots.Count);
    });
}

static async Task DeleteWorldRefusalsAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        await ThrowsAsync<InvalidOperationException>(() => CreateDeletion(fixture).DeleteAsync(0));
        await ThrowsAsync<InvalidOperationException>(() => CreateDeletion(fixture, running: () => true).DeleteAsync(1));
        True(Directory.Exists(fixture.ParkedPath), "refusal changed parked world");
        await File.WriteAllTextAsync(fixture.Env.Context.StatePaths.PendingOperationPath, "{}");
        await ThrowsAsync<InvalidOperationException>(() => CreateDeletion(fixture).DeleteAsync(1));
        File.Delete(fixture.Env.Context.StatePaths.PendingOperationPath);
        var registry = await fixture.Env.Slots.LoadAsync();
        registry.Slots[1].WorldGuid = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        await fixture.Env.Slots.SaveAsync(registry);
        await ThrowsAsync<InvalidOperationException>(() => CreateDeletion(fixture).DeleteAsync(1));
        True(Directory.Exists(fixture.ParkedPath) && File.Exists(fixture.Env.Context.StatePaths.WorldProfilePath(1)), "identity refusal changed data");
    });
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        var blockedBackup = CreateDeleteBackup(fixture, capacityProbe: (_, _) => false);
        await ThrowsAsync<IOException>(() => CreateDeletion(fixture, blockedBackup).DeleteAsync(1));
        True(Directory.Exists(fixture.ParkedPath), "snapshot failure changed world");
        Equal(2, (await fixture.Env.Slots.LoadAsync()).Slots.Count);
        True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "snapshot failure created journal");
    });
}

static async Task DeleteWorldFailureAsync()
{
    foreach (var failure in new[] { DeleteWorldCheckpoint.WorldStaged, DeleteWorldCheckpoint.MetadataUpdated, DeleteWorldCheckpoint.BeforePurge })
    {
        await InTempAsync(async root =>
        {
            var fixture = await CreateDeleteFixtureAsync(root);
            var service = CreateDeletion(fixture, checkpoint: point => { if (point == failure) throw new IOException("synthetic transaction failure"); });
            await ThrowsAsync<IOException>(() => service.DeleteAsync(1));
            await AssertDeleteRolledBackAsync(fixture);
            True(Directory.EnumerateDirectories(fixture.Env.Context.StatePaths.WorldBackupRoot(fixture.ParkedUid), "before-delete-*").Any(), "recovery snapshot lost on failed transaction");
        });
    }
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        var service = CreateDeletion(fixture, purge: path =>
        {
            File.Delete(Path.Combine(path, fixture.ParkedUid, "Level.sav"));
            throw new IOException("synthetic partial purge");
        });
        await ThrowsAsync<IOException>(() => service.DeleteAsync(1));
        await AssertDeleteRolledBackAsync(fixture);
    });
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root);
        var service = CreateDeletion(fixture, purge: _ =>
        {
            Directory.CreateDirectory(fixture.ParkedPath); // external path claim during transaction
            File.WriteAllText(Path.Combine(fixture.ParkedPath, "foreign.txt"), "not the registered world");
            throw new IOException("synthetic ambiguous path");
        });
        await ThrowsAsync<AggregateException>(() => service.DeleteAsync(1));
        True(File.Exists(Path.Combine(fixture.ParkedPath, "foreign.txt")), "ambiguous external data was removed");
        True(Directory.EnumerateDirectories(fixture.Env.Context.ServerPaths.SaveRoot, ".pal-delete-*").Any(), "staged original was removed despite ambiguity");
        True(File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "ambiguous recovery lost transaction authority");
    });
}

static async Task AssertDeleteRolledBackAsync(DeleteFixture fixture)
{
    True(File.Exists(Path.Combine(fixture.ParkedPath, fixture.ParkedUid, "Level.sav")), "failed delete lost world data");
    True(File.Exists(Path.Combine(fixture.ParkedPath, fixture.ParkedUid, "backup", "world", "native.sav")), "failed delete lost built-in backup");
    Equal(2, (await fixture.Env.Slots.LoadAsync()).Slots.Count);
    True(File.Exists(fixture.Env.Context.StatePaths.WorldProfilePath(1)), "failed delete lost profile");
    True((await ReadBuildAsync(fixture.Env)).Worlds.ContainsKey(fixture.ParkedUid), "failed delete lost build state");
    True((await ReadActivityAsync(fixture.Env)).Worlds.ContainsKey(fixture.ParkedUid), "failed delete lost player activity");
    True(!File.Exists(fixture.Env.Context.StatePaths.PendingOperationPath), "verified rollback retained journal");
    await new WorldIdentityAuditService(fixture.Env.Context, fixture.Env.Slots, fixture.Env.WorldSettings).AuditOrThrowAsync(await fixture.Env.Slots.LoadAsync());
}

static BackupService CreateDeleteBackup(DeleteFixture fixture, Func<string, long, bool>? capacityProbe = null) =>
    new(fixture.Env.Context, fixture.Env.Processes, fixture.Env.Slots, fixture.Env.WorldSettings, fixture.Env.Files, fixture.Env.Log, capacityProbe: capacityProbe);

static DeleteWorldService CreateDeletion(DeleteFixture fixture, BackupService? backup = null,
    Action<DeleteWorldCheckpoint>? checkpoint = null, Func<bool>? running = null, Action<string>? purge = null) =>
    new(fixture.Env.Context, fixture.Env.Processes, fixture.Env.Slots,
        new WorldIdentityAuditService(fixture.Env.Context, fixture.Env.Slots, fixture.Env.WorldSettings),
        backup ?? CreateDeleteBackup(fixture), fixture.Env.Files,
        new OperationJournalService(fixture.Env.Context, fixture.Env.Files), fixture.Env.Log,
        checkpoint, running, purge);

static async Task<DeleteFixture> CreateDeleteFixtureAsync(string root)
{
    var env = CreateEnvironment(root);
    const string activeUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    const string parkedUid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    var registry = Registry(activeUid, parkedUid);
    var active = Path.Combine(env.Context.ServerPaths.ActivePath, activeUid);
    var parkedPath = Path.Combine(env.Context.ServerPaths.SaveRoot, registry.Slots[1].ParkedFolder);
    var parked = Path.Combine(parkedPath, parkedUid);
    CreateWorld(active, "active-world"); CreateWorld(parked, "parked-world");
    Directory.CreateDirectory(Path.Combine(parked, "backup", "world"));
    await File.WriteAllTextAsync(Path.Combine(parked, "backup", "world", "native.sav"), "native-backup");
    await File.WriteAllTextAsync(Path.Combine(parked, "WorldOption.sav"), "option-data");
    await File.WriteAllTextAsync(Path.Combine(parked, "Players", "person_dps.sav"), "dps-data");
    Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!);
    await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={activeUid}");
    await env.Slots.SaveAsync(registry);
    await WriteProfileAsync(env, registry.Slots[0], activeUid);
    await WriteProfileAsync(env, registry.Slots[1], parkedUid);
    var build = new BuildStateDocument();
    build.Worlds[activeUid] = new WorldBuildState { SlotId = 0, WorldUid = activeUid };
    build.Worlds[parkedUid] = new WorldBuildState { SlotId = 1, WorldUid = parkedUid };
    await env.Files.WriteJsonAsync(env.Context.StatePaths.BuildStatePath, build);
    var activity = new PlayerActivityDocument();
    activity.Worlds[activeUid] = new PlayerWorldActivity();
    activity.Worlds[parkedUid] = new PlayerWorldActivity();
    await env.Files.WriteJsonAsync(env.Context.StatePaths.PlayerActivityPath, activity);
    return new DeleteFixture(env, activeUid, parkedUid, parkedPath);
}

static async Task<BuildStateDocument> ReadBuildAsync(TestEnvironment env) =>
    JsonSerializer.Deserialize<BuildStateDocument>(await File.ReadAllBytesAsync(env.Context.StatePaths.BuildStatePath))!;
static async Task<PlayerActivityDocument> ReadActivityAsync(TestEnvironment env) =>
    JsonSerializer.Deserialize<PlayerActivityDocument>(await File.ReadAllBytesAsync(env.Context.StatePaths.PlayerActivityPath))!;

static Task WriteSyntheticIniAsync(string serverRoot)
{
    var ini = Path.Combine(serverRoot, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
    Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
    return File.WriteAllTextAsync(ini, "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(" +
        string.Join(',', SyntheticProfileValues().Select(pair => pair.Key + "=" + pair.Value)) + ")");
}

static async Task FreshPcAttachAsync()
{
    await InTempAsync(async root =>
    {
        var serverRoot = Path.Combine(root, "CopiedPalServer"); FakeServer(serverRoot);
        await WriteSyntheticIniAsync(serverRoot);
        var saveRoot = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames");
        const string a = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", b = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var active = Path.Combine(saveRoot, "0", a);
        var parked = Path.Combine(saveRoot, "0 - Slot 004 - manually copied", b);
        CreateWorld(active, "copied-active"); CreateWorld(parked, "copied-parked");
        await File.WriteAllBytesAsync(Path.Combine(parked, "Players", "person_dps.sav"), [1, 7, 9]);
        var before = SnapshotFiles(saveRoot);
        var app = AppPaths.ForExplicitRoots(Path.Combine(root, "App"), Path.Combine(root, "FreshState"));
        True(!File.Exists(app.ServerRegistryPath) && !Directory.Exists(app.ServersStateRoot), "fresh PC already had Manager state");
        var files = new SafeFileService();
        var registration = new ServerRegistryService(app, files);
        var registered = await registration.RegisterFromExeAsync(Path.Combine(serverRoot, "PalServer.exe"), "copied server");
        Equal(registered.Id, (await registration.LoadAsync()).SelectedServerId!);
        var service = new ServerStateService(app, files);
        Equal(1, service.DiscoverWorlds(registered).Count);
        await service.AttachAsync(registered);
        Equal(ServerStateKind.Ready, (await new ServerStateService(app, files).AssessAsync(registered)).Kind);
        var state = ServerStatePaths.ForRegisteredServer(app, registered);
        var registry = JsonSerializer.Deserialize<SaveSlotRegistry>(await File.ReadAllTextAsync(state.RegistryPath))!;
        Equal(1, registry.Slots.Count); Equal(a, registry.Slots.Single(slot => slot.Id == registry.ActiveSlotId).WorldGuid);
        Equal(SaveSlotService.NewParkedFolderName(0, "世界 0"), registry.Slots.Single().ParkedFolder);
        True(registry.Slots.All(slot => File.Exists(state.WorldProfilePath(slot.Id))), "attached profiles missing");
        var activeProfile = JsonSerializer.Deserialize<WorldSettingsProfile>(await File.ReadAllTextAsync(state.WorldProfilePath(0)))!;
        Equal(SyntheticProfileValues()["ExpRate"], activeProfile.Values["ExpRate"]);
        var report = await new WorldDiscoveryService(new ServerPaths(serverRoot), state).ScanAsync(registry);
        Equal(WorldDiscoveryStatus.Importable, report.Worlds.Single(world => world.WorldUid == b).Status);
        EqualFileSnapshots(before, SnapshotFiles(saveRoot));
    });
}

static async Task WorldDiscoveryAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root); var env = fixture.Env;
        var saveRoot = env.Context.ServerPaths.SaveRoot;
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        var registry = await env.Slots.LoadAsync();
        var initial = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.RegisteredHealthy, initial.Worlds.Single(world => world.FolderName == "0").Status);
        Equal(WorldDiscoveryStatus.RegisteredHealthy, initial.Worlds.Single(world => world.FolderName == registry.Slots[1].ParkedFolder).Status);
        True(initial.Profiles.All(profile => profile.Status == WorldProfileStatus.ProfileHealthy), "healthy profiles not recognized");
        const string c = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", d = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
        var extra = Path.Combine(saveRoot, "manual-extra"); CreateWorld(Path.Combine(extra, c), "extra");
        await File.WriteAllTextAsync(Path.Combine(extra, c, "WorldOption.sav"), "option");
        var report = await discovery.ScanAsync(registry);
        var candidate = report.Worlds.Single(world => world.FolderName == "manual-extra");
        Equal(WorldDiscoveryStatus.Importable, candidate.Status);
        True(candidate.CanImport && candidate.HasWorldOption && candidate.UnusualFolderName, "world option or import flag missing");
        CreateWorld(Path.Combine(saveRoot, "duplicate-registered", fixture.ActiveUid), "duplicate");
        CreateWorld(Path.Combine(saveRoot, "duplicate-unknown", c), "duplicate");
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.DuplicateUid, report.Worlds.Single(world => world.FolderName == "duplicate-registered").Status);
        Equal(WorldDiscoveryStatus.DuplicateUid, report.Worlds.Single(world => world.FolderName == "manual-extra").Status);
        Equal(WorldDiscoveryStatus.DuplicateUid, report.Worlds.Single(world => world.FolderName == "duplicate-unknown").Status);
        var incomplete = Path.Combine(saveRoot, "incomplete"); CreateWorld(Path.Combine(incomplete, d), "incomplete");
        File.Delete(Path.Combine(incomplete, d, "LevelMeta.sav"));
        var ambiguous = Path.Combine(saveRoot, "ambiguous"); CreateWorld(Path.Combine(ambiguous, d), "first");
        CreateWorld(Path.Combine(ambiguous, "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE"), "second");
        CreateWorld(Path.Combine(saveRoot, ".pal-switch-evidence", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), "ignored");
        CreateWorld(Path.Combine(extra, c, "backup", "world", "11111111111111111111111111111111"), "native backup");
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.Incomplete, report.Worlds.Single(world => world.FolderName == "incomplete").Status);
        Equal(WorldDiscoveryStatus.Incomplete, report.Worlds.Single(world => world.FolderName == "ambiguous").Status);
        True(report.Worlds.All(world => !world.FolderName.StartsWith(".pal-", StringComparison.Ordinal)), "transaction artifact discovered");
        True(report.Worlds.All(world => world.WorldUid != "11111111111111111111111111111111"), "nested backup discovered");
        var parkedMeta = Path.Combine(fixture.ParkedPath, fixture.ParkedUid, "LevelMeta.sav");
        File.Delete(parkedMeta);
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.RegisteredIncomplete, report.Worlds.Single(world => world.SlotId == 1).Status);
        await File.WriteAllTextAsync(parkedMeta, "synthetic-meta");
        Directory.Delete(fixture.ParkedPath, true);
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.RegisteredMissing, report.Worlds.Single(world => world.SlotId == 1).Status);
        CreateWorld(Path.Combine(fixture.ParkedPath, d), "wrong world");
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.RegisteredIdentityMismatch, report.Worlds.Single(world => world.SlotId == 1).Status);
        True(!report.Worlds.Single(world => world.SlotId == 1).CanImport, "mismatched registered world offered for import");
        Directory.Delete(env.Context.ServerPaths.ActivePath, true);
        CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, d), "wrong active world");
        report = await discovery.ScanAsync(registry);
        Equal(WorldDiscoveryStatus.RegisteredIdentityMismatch, report.Worlds.Single(world => world.FolderName == "0").Status);
        True(report.Worlds.All(world => world.FolderName != "0" || !world.CanImport), "mismatched active world offered for import");
    });
}

static async Task ReadOnlyRescanAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root); var env = fixture.Env;
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        var registry = await env.Slots.LoadAsync();
        var beforeSaves = SnapshotFiles(env.Context.ServerPaths.SaveRoot);
        var beforeState = SnapshotFiles(env.Context.StatePaths.StateRoot);
        var first = await discovery.ScanAsync(registry); var second = await discovery.ScanAsync(registry);
        Equal(string.Join('|', first.Worlds.Select(world => world.Status)), string.Join('|', second.Worlds.Select(world => world.Status)));
        EqualFileSnapshots(beforeSaves, SnapshotFiles(env.Context.ServerPaths.SaveRoot));
        EqualFileSnapshots(beforeState, SnapshotFiles(env.Context.StatePaths.StateRoot));
        var profilePath = env.Context.StatePaths.WorldProfilePath(1);
        File.Delete(profilePath);
        Equal(WorldProfileStatus.ProfileMissing, (await discovery.ScanAsync(registry)).Profiles.Single(profile => profile.SlotId == 1).Status);
        await File.WriteAllTextAsync(profilePath, "{broken");
        Equal(WorldProfileStatus.ProfileInvalid, (await discovery.ScanAsync(registry)).Profiles.Single(profile => profile.SlotId == 1).Status);
        await WriteProfileAsync(env, registry.Slots[1], "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
        Equal(WorldProfileStatus.ProfileIdentityMismatch, (await discovery.ScanAsync(registry)).Profiles.Single(profile => profile.SlotId == 1).Status);
        var orphan = env.Context.StatePaths.WorldProfilePath(99);
        await File.WriteAllTextAsync(orphan, "{}");
        Equal(WorldProfileStatus.OrphanProfile, (await discovery.ScanAsync(registry)).Profiles.Single(profile => profile.Path == orphan).Status);
        True(File.Exists(profilePath) && File.Exists(orphan), "rescan repaired or deleted profiles");
    });
}

static async Task ExistingWorldImportAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root); var env = fixture.Env;
        var registry = await env.Slots.LoadAsync(); registry.NextSlotId = 3; await env.Slots.SaveAsync(registry);
        await WriteSyntheticIniAsync(env.Context.ServerPaths.ServerRoot);
        var runtime = await PalWorldIniDocument.LoadAsync(env.Context.ServerPaths.SettingsPath);
        runtime.SetValue("ExpRate", "7.000000"); await runtime.WriteAtomicAsync(env.Context.ServerPaths.SettingsPath);
        var defaultIni = Path.Combine(env.Context.ServerPaths.ServerRoot, "DefaultPalWorldSettings.ini");
        File.Copy(env.Context.ServerPaths.SettingsPath, defaultIni);
        var defaults = await PalWorldIniDocument.LoadAsync(defaultIni); defaults.SetValue("ExpRate", "9.000000");
        await defaults.WriteAtomicAsync(defaultIni);
        var external = Path.Combine(root, "other-PalWorldSettings.ini"); File.Copy(defaultIni, external);
        var other = await PalWorldIniDocument.LoadAsync(external); other.SetValue("ExpRate", "11.000000");
        await other.WriteAtomicAsync(external);
        var sourceProfile = JsonSerializer.Deserialize<WorldSettingsProfile>(await File.ReadAllTextAsync(env.Context.StatePaths.WorldProfilePath(1)))!;
        sourceProfile.Values["ExpRate"] = "5.000000";
        await env.Files.WriteJsonAsync(env.Context.StatePaths.WorldProfilePath(1), sourceProfile);
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        var importer = NewImporter(env, discovery);
        var originalActive = SnapshotFiles(env.Context.ServerPaths.ActivePath);
        var originalParked = SnapshotFiles(fixture.ParkedPath);
        var runtimeBefore = await File.ReadAllBytesAsync(env.Context.ServerPaths.SettingsPath);
        var custom = SyntheticProfileValues(); custom["ExpRate"] = "13.000000";
        var externalRoot = Path.Combine(root, "old-server", "SaveGames");
        var cases = new (string Uid, ExistingWorldSettingsChoice Choice, string ExpectedRate)[]
        {
            ("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", new(ExistingWorldSettingsMode.ActiveProfile), SyntheticProfileValues()["ExpRate"]),
            ("DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", new(ExistingWorldSettingsMode.OtherProfile, SourceSlotId: 1), "5.000000"),
            ("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", new(ExistingWorldSettingsMode.CurrentServerIni), "7.000000"),
            ("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", new(ExistingWorldSettingsMode.GameDefaults), "9.000000"),
            ("11111111111111111111111111111111", new(ExistingWorldSettingsMode.Custom, CustomValues: custom), "13.000000"),
            ("22222222222222222222222222222222", new(ExistingWorldSettingsMode.ExternalIni, ExternalIniPath: external), "11.000000")
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var sourceContainer = index == 0
                ? Path.Combine(env.Context.ServerPaths.SaveRoot, "manual-extra")
                : Path.Combine(externalRoot, index == 1 ? "0" : $"old-world-{index}");
            var world = Path.Combine(sourceContainer, item.Uid);
            CreateWorld(world, "import-data-" + index);
            await File.WriteAllBytesAsync(Path.Combine(world, "Players", "person_dps.sav"), [3, 4, 5, (byte)index]);
            if (index == 1) await File.WriteAllTextAsync(Path.Combine(world, "WorldOption.sav"), "option-data");
            var before = SnapshotFiles(world);
            var registryBeforeScan = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
            var selection = index == 3 ? world : sourceContainer; // direct UID selection is also supported.
            var report = await discovery.ScanSourceAsync(selection, await env.Slots.LoadAsync());
            Equal(WorldDiscoveryStatus.Importable, report.Worlds.Single().Status);
            var registryAfterScan = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
            True(registryBeforeScan.SequenceEqual(registryAfterScan), "rescan auto-registered world");
            var result = await importer.ImportAsync(selection, item.Uid, "tag-" + index, item.Choice);
            var slot = result.Slot;
            Equal(index + 3, slot.Id);
            Equal(SaveSlotService.NewParkedFolderName(slot.Id, slot.Tag), slot.ParkedFolder);
            Equal(item.ExpectedRate, (await env.WorldSettings.LoadProfileAsync(slot.Id, item.Uid)).Values["ExpRate"]);
            var destinationWorld = Path.Combine(env.Context.ServerPaths.SaveRoot, slot.ParkedFolder, item.Uid);
            if (index == 1)
            {
                var disabled = Directory.EnumerateFiles(destinationWorld, "WorldOption.sav.disabled-*").Single();
                Equal("option-data", await File.ReadAllTextAsync(disabled));
                True(!File.Exists(Path.Combine(destinationWorld, "WorldOption.sav")), "imported WorldOption remains active");
                var copied = SnapshotFiles(destinationWorld);
                foreach (var file in before.Where(file => file.Key != "WorldOption.sav"))
                    True(copied.TryGetValue(file.Key, out var hash) && hash == file.Value, "imported file changed: " + file.Key);
            }
            else EqualFileSnapshots(before, SnapshotFiles(destinationWorld));
            if (index == 0) True(!Directory.Exists(sourceContainer), "old noncanonical in-server source was not cleaned");
            else EqualFileSnapshots(before, SnapshotFiles(world));
            True(result.CleanupWarning is null, "unexpected import cleanup warning");
            EqualFileSnapshots(originalActive, SnapshotFiles(env.Context.ServerPaths.ActivePath));
            EqualFileSnapshots(originalParked, SnapshotFiles(fixture.ParkedPath));
            var runtimeAfter = await File.ReadAllBytesAsync(env.Context.ServerPaths.SettingsPath);
            True(runtimeBefore.SequenceEqual(runtimeAfter), "import changed global/runtime INI");
            var after = await env.Slots.LoadAsync();
            Equal(index + 4, after.NextSlotId);
            True(after.Slots.Any(existing => existing.Id == 0 && existing.WorldGuid == fixture.ActiveUid) &&
                 after.Slots.Any(existing => existing.Id == 1 && existing.WorldGuid == fixture.ParkedUid), "existing slots changed");
        }
        CreateWorld(Path.Combine(externalRoot, "another-0", "33333333333333333333333333333333"), "batch-a");
        CreateWorld(Path.Combine(externalRoot, "another-1", "44444444444444444444444444444444"), "batch-b");
        CreateWorld(Path.Combine(externalRoot, "backup", "55555555555555555555555555555555"), "ignored-history");
        var batch = await discovery.ScanSourceAsync(externalRoot, await env.Slots.LoadAsync());
        Equal(2, batch.ImportableCount);
        True(batch.Worlds.All(world => world.FolderName != "backup"), "source scan descended into backup history");
        var retainedSource = Path.Combine(env.Context.ServerPaths.SaveRoot, "manual-with-extra");
        const string retainedUid = "66666666666666666666666666666666";
        CreateWorld(Path.Combine(retainedSource, retainedUid), "retained-source");
        await File.WriteAllTextAsync(Path.Combine(retainedSource, "unrelated-note.txt"), "keep me");
        var retained = await importer.ImportAsync(retainedSource, retainedUid, "retained", new(ExistingWorldSettingsMode.ActiveProfile));
        True(retained.CleanupWarning is not null && Directory.Exists(retainedSource), "extra source content was silently deleted");
        True(File.Exists(Path.Combine(env.Context.ServerPaths.SaveRoot, retained.Slot.ParkedFolder, retainedUid, "Level.sav")),
            "successful canonical copy was rolled back because old source was retained");
    });
}

static async Task CanonicalWorldAdoptionAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        const string activeUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string parkedUid = "55555555555555555555555555555555";
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots =
        [new SaveSlot { Id = 0, Tag = "current", WorldGuid = activeUid, ParkedFolder = "0 - Slot 000 - current" }] };
        CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, activeUid), "active");
        var canonicalFolder = Path.Combine(env.Context.ServerPaths.SaveRoot, "0 - Slot 005 - Raid World");
        var world = Path.Combine(canonicalFolder, parkedUid);
        CreateWorld(world, "copied-parked-world");
        await File.WriteAllTextAsync(Path.Combine(world, "WorldOption.sav"), "original-option-bytes");
        Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!);
        await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={activeUid}");
        await env.Slots.SaveAsync(registry);
        await WriteProfileAsync(env, registry.Slots[0], activeUid);
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        var beforeScanSaves = SnapshotFiles(env.Context.ServerPaths.SaveRoot);
        var beforeScanState = SnapshotFiles(env.Context.StatePaths.StateRoot);
        var report = await discovery.ScanAsync(registry);
        var candidate = report.Worlds.Single(item => item.WorldUid == parkedUid);
        True(candidate.CanImport, "canonical copied world was not reported as importable");
        EqualFileSnapshots(beforeScanSaves, SnapshotFiles(env.Context.ServerPaths.SaveRoot));
        EqualFileSnapshots(beforeScanState, SnapshotFiles(env.Context.StatePaths.StateRoot));

        var result = await NewImporter(env, discovery).ImportAsync(candidate.FolderPath, parkedUid, "ignored UI tag",
            new(ExistingWorldSettingsMode.ActiveProfile));
        Equal(5, result.Slot.Id);
        Equal("Raid World", result.Slot.Tag);
        Equal("0 - Slot 005 - Raid World", result.Slot.ParkedFolder);
        True(Directory.Exists(world), "adoption moved or replaced the UID path");
        Equal(2, Directory.EnumerateDirectories(env.Context.ServerPaths.SaveRoot).Count());
        True(!File.Exists(Path.Combine(world, "WorldOption.sav")), "adopted WorldOption remained active");
        Equal("original-option-bytes", await File.ReadAllTextAsync(Directory.EnumerateFiles(world, "WorldOption.sav.disabled-*").Single()));
        var after = await env.Slots.LoadAsync();
        Equal(6, after.NextSlotId);
        True(after.Slots.Any(slot => slot.Id == 5 && slot.WorldGuid == parkedUid), "adopted slot was not registered");
        var profile = await env.WorldSettings.LoadProfileAsync(5, parkedUid);
        Equal("Raid World", profile.Tag);
    });
}

static async Task CanonicalAdoptionWorldOptionRecoveryAsync()
{
    await InTempAsync(async root =>
    {
        var env = CreateEnvironment(root);
        const string activeUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string parkedUid = "55555555555555555555555555555555";
        var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots =
        [new SaveSlot { Id = 0, Tag = "current", WorldGuid = activeUid, ParkedFolder = "0 - Slot 000 - current" }] };
        CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, activeUid), "active");
        var canonicalFolder = Path.Combine(env.Context.ServerPaths.SaveRoot, "0 - Slot 005 - Raid World");
        var world = Path.Combine(canonicalFolder, parkedUid);
        CreateWorld(world, "copied-parked-world");
        var worldOption = Path.Combine(world, "WorldOption.sav");
        await File.WriteAllBytesAsync(worldOption, [1, 3, 5, 7, 9]);
        Directory.CreateDirectory(Path.GetDirectoryName(env.Context.ServerPaths.UserSettingsPath)!);
        await File.WriteAllTextAsync(env.Context.ServerPaths.UserSettingsPath, $"DedicatedServerName={activeUid}");
        await env.Slots.SaveAsync(registry);
        await WriteProfileAsync(env, registry.Slots[0], activeUid);
        var registryBefore = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
        var worldBefore = SnapshotFiles(world);
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        var importer = NewImporter(env, discovery, checkpoint: point =>
        {
            if (point == WorldImportCheckpoint.AfterWorldOptionRename) throw new IOException("synthetic post-rename failure");
        });

        await ThrowsAsync<IOException>(() => importer.ImportAsync(canonicalFolder, parkedUid, "ignored",
            new(ExistingWorldSettingsMode.ActiveProfile)));
        var registryAfter = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
        True(registryBefore.SequenceEqual(registryAfter),
            "failed adoption committed registry changes");
        EqualFileSnapshots(worldBefore, SnapshotFiles(world));
        var restoredOption = await File.ReadAllBytesAsync(worldOption);
        True(File.Exists(worldOption) && restoredOption.SequenceEqual(new byte[] { 1, 3, 5, 7, 9 }),
            "WorldOption filename or bytes were not restored");
        True(!File.Exists(env.WorldSettings.GetProfilePath(5)), "failed adoption retained an uncommitted profile");
        True(!File.Exists(env.Context.StatePaths.PendingOperationPath), "verified recovery retained the operation journal");
    });
}

static async Task WorldImportRefusalsAsync()
{
    await InTempAsync(async root =>
    {
        var fixture = await CreateDeleteFixtureAsync(root); var env = fixture.Env;
        var discovery = new WorldDiscoveryService(env.Context.ServerPaths, env.Context.StatePaths);
        const string uid = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        var folder = Path.Combine(env.Context.ServerPaths.SaveRoot, "manual-candidate");
        var world = Path.Combine(folder, uid); CreateWorld(world, "candidate");
        var originalFiles = SnapshotFiles(folder);
        var registryBefore = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
        var choice = new ExistingWorldSettingsChoice(ExistingWorldSettingsMode.ActiveProfile);
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery, running: () => true).ImportAsync(folder, uid, "tag", choice));
        await File.WriteAllTextAsync(env.Context.StatePaths.PendingOperationPath, "{}");
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery).ImportAsync(folder, uid, "tag", choice));
        File.Delete(env.Context.StatePaths.PendingOperationPath);
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery).ImportAsync(folder, "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", "tag", choice));
        CreateWorld(Path.Combine(env.Context.ServerPaths.SaveRoot, "duplicate-candidate", uid), "copy");
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery).ImportAsync(folder, uid, "tag", choice));
        Directory.Delete(Path.Combine(env.Context.ServerPaths.SaveRoot, "duplicate-candidate"), true);
        File.Delete(Path.Combine(world, "LevelMeta.sav"));
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery).ImportAsync(folder, uid, "tag", choice));
        await File.WriteAllTextAsync(Path.Combine(world, "LevelMeta.sav"), "synthetic-meta");
        await ThrowsAsync<InvalidOperationException>(() => NewImporter(env, discovery).ImportAsync(folder, uid, " ", choice));
        foreach (var failure in new[] { WorldImportCheckpoint.BeforeProfileWrite, WorldImportCheckpoint.BeforeRegistryCommit })
        {
            var importer = NewImporter(env, discovery, checkpoint: point => { if (point == failure) throw new IOException("synthetic metadata fault"); });
            await ThrowsAsync<IOException>(() => importer.ImportAsync(folder, uid, "tag", choice));
            var registryAfterFailure = await File.ReadAllBytesAsync(env.Context.StatePaths.RegistryPath);
            True(registryBefore.SequenceEqual(registryAfterFailure), "failed import changed registry");
            True(!File.Exists(env.Context.StatePaths.WorldProfilePath(2)), "failed import left active profile");
            True(!File.Exists(env.Context.StatePaths.PendingOperationPath), "failed import left transaction despite verified rollback");
        }
        EqualFileSnapshots(originalFiles, SnapshotFiles(folder));
        True(!Directory.EnumerateDirectories(env.Context.ServerPaths.SaveRoot, ".pal-import-*").Any(), "failed import left staging content");
        True(!Directory.Exists(Path.Combine(env.Context.ServerPaths.SaveRoot, SaveSlotService.NewParkedFolderName(2, "tag"))),
            "failed import left canonical destination");
    });
}

static WorldImportService NewImporter(TestEnvironment env, WorldDiscoveryService discovery,
    Action<WorldImportCheckpoint>? checkpoint = null, Func<bool>? running = null) =>
    new(env.Context, env.Processes, env.Slots, env.WorldSettings,
        new WorldIdentityAuditService(env.Context, env.Slots, env.WorldSettings), discovery,
        new WorldOptionService(env.Slots, env.Log), new OperationJournalService(env.Context, env.Files), env.Log, checkpoint, running);

static Dictionary<string, string> SnapshotFiles(string root) => Directory.Exists(root)
    ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase)
    : new(StringComparer.OrdinalIgnoreCase);

static void EqualFileSnapshots(Dictionary<string, string> expected, Dictionary<string, string> actual)
{
    Equal(expected.Count, actual.Count);
    foreach (var item in expected) True(actual.TryGetValue(item.Key, out var hash) && hash == item.Value, "file changed: " + item.Key);
}

static void FakeServer(string path)
{
    Directory.CreateDirectory(Path.Combine(path, "Pal", "Saved"));
    File.WriteAllBytes(Path.Combine(path, "PalServer.exe"), [1]);
}

static Dictionary<string,string> SyntheticProfileValues() => WorldSettingsService.ProfileDefinitions.ToDictionary(def => def.Key, def => def.Kind switch
{
    WorldSettingKind.Boolean => "False", WorldSettingKind.Choice => def.Choices![0],
    WorldSettingKind.Integer => Math.Ceiling(def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture),
    WorldSettingKind.Number => Math.Max(def.Minimum, def.Minimum == 0 ? 1 : def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture),
    _ => ""
}, StringComparer.OrdinalIgnoreCase);

static string FindSourceRoot()
{
    for (var cursor = new DirectoryInfo(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
        if (File.Exists(Path.Combine(cursor.FullName, "PalServerManager.WinUI", "PalServerManager.WinUI.csproj"))) return cursor.FullName;
    throw new DirectoryNotFoundException("Cannot locate source checkout from test output directory.");
}

static TestEnvironment CreateEnvironment(string root)
{
    var server = Path.Combine(root, "PalServer"); var app = Path.Combine(root, "App"); var state = Path.Combine(root, "State");
    Directory.CreateDirectory(server); Directory.CreateDirectory(app); Directory.CreateDirectory(state);
    File.WriteAllBytes(Path.Combine(server, "PalServer.exe"), [0]);
    var registered = RegisteredServer.Create("synthetic server", server);
    var context = PalContext.ForRegisteredServer(new AppPaths(app, state), registered);
    var files = new SafeFileService(); var log = new LoggingService(context);
    var processes = new ServerProcessService(context, log); var slots = new SaveSlotService(context, processes, log, files);
    var worldSettings = new WorldSettingsService(context, log, files);
    return new TestEnvironment(context, files, log, processes, slots, worldSettings);
}

static SaveSlotRegistry Registry(string uid0, string uid1) => new()
{
    ActiveSlotId = 0, NextSlotId = 2,
    Slots =
    [
        new SaveSlot { Id = 0, Tag = "zero", WorldGuid = uid0, ParkedFolder = "0 - Slot 000 - zero" },
        new SaveSlot { Id = 1, Tag = "one", WorldGuid = uid1, ParkedFolder = "0 - Slot 001 - one" }
    ]
};

static void CreateWorld(string path, string level)
{ Directory.CreateDirectory(Path.Combine(path, "Players")); File.WriteAllText(Path.Combine(path, "Level.sav"), level); File.WriteAllText(Path.Combine(path, "LevelMeta.sav"), "synthetic-meta"); }

static Task WriteProfileAsync(TestEnvironment env, SaveSlot slot, string uid)
{
    var values = WorldSettingsService.ProfileDefinitions.ToDictionary(def => def.Key, def => def.Kind switch
    {
        WorldSettingKind.Boolean => "False", WorldSettingKind.Choice => def.Choices![0], WorldSettingKind.Integer => Math.Ceiling(def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture),
        WorldSettingKind.Number => Math.Max(def.Minimum, def.Minimum == 0 ? 1 : def.Minimum).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => ""
    }, StringComparer.OrdinalIgnoreCase);
    var profile = new WorldSettingsProfile { SlotId = slot.Id, WorldGuid = uid, Tag = slot.Tag, Values = values };
    return env.Files.WriteJsonAsync(env.WorldSettings.GetProfilePath(slot.Id), profile);
}

static async Task InTempAsync(Func<string, Task> body)
{
    var path = Path.Combine(FindSourceRoot(), "TestRuns", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try { await body(path); } finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void True(bool value, string message) { if (!value) throw new Exception(message); }
sealed record TestEnvironment(PalContext Context, SafeFileService Files, LoggingService Log, ServerProcessService Processes, SaveSlotService Slots, WorldSettingsService WorldSettings);
sealed record RestoreFixture(TestEnvironment Env, SaveSlotRegistry Registry, string World, BackupEntry Native);
sealed record DeleteFixture(TestEnvironment Env, string ActiveUid, string ParkedUid, string ParkedPath);
