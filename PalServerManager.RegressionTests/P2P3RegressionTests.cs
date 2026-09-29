using System.Globalization;
using HaoHaoTianTian.PalHR.Models;
using HaoHaoTianTian.PalHR.Services;

partial class Program
{
    private static readonly bool P2P3RegressionInitialized = P2P3RegressionTests.Run();
}

internal static class P2P3RegressionTests
{
    internal static bool Run()
    {
        Task.Run(RunAsync).GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync()
    {
        Console.WriteLine("RUN P2/P3 world-setting ranges");
        WorldSettingRangesAreEnforced();
        Console.WriteLine("PASS P2/P3 world-setting ranges");

        Console.WriteLine("RUN P2/P3 active rename collision");
        await ActiveRenameCollisionIsRejectedAsync();
        Console.WriteLine("PASS P2/P3 active rename collision");

        Console.WriteLine("RUN P2/P3 journal completion authority");
        await JournalCompletionRemovesAuthorityLastAsync();
        Console.WriteLine("PASS P2/P3 journal completion authority");

        Console.WriteLine("RUN P2/P3 first-attach cleanup");
        await FirstAttachCleansPreservedEmptyStateAsync();
        Console.WriteLine("PASS P2/P3 first-attach cleanup");
    }

    private static void WorldSettingRangesAreEnforced()
    {
        var valid = CreateValidProfileValues();
        WorldSettingsService.ValidateProfileValues(valid);

        var bounded = WorldSettingsService.ProfileDefinitions.First(definition =>
            definition.Kind is WorldSettingKind.Number or WorldSettingKind.Integer &&
            double.IsFinite(definition.Maximum));
        var invalid = new Dictionary<string, string>(valid, StringComparer.OrdinalIgnoreCase)
        {
            [bounded.Key] = (bounded.Maximum + 1).ToString(CultureInfo.InvariantCulture)
        };
        Throws<InvalidOperationException>(() => WorldSettingsService.ValidateProfileValues(invalid));

        var document = PalWorldIniDocument.Parse("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerPlayerMaxNum=32)");
        Throws<InvalidOperationException>(() => document.SetValue("ServerPlayerMaxNum", "129"));
        document.SetValue("ServerPlayerMaxNum", "128");
    }

    private static async Task ActiveRenameCollisionIsRejectedAsync()
    {
        await InTempAsync(async root =>
        {
            var env = CreateEnvironment(root);
            const string uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var slot = new SaveSlot
            {
                Id = 0,
                Tag = "old",
                WorldGuid = uid,
                ParkedFolder = SaveSlotService.NewParkedFolderName(0, "old")
            };
            var registry = new SaveSlotRegistry { ActiveSlotId = 0, NextSlotId = 1, Slots = [slot] };
            CreateWorld(Path.Combine(env.Context.ServerPaths.ActivePath, uid));
            await env.Slots.SaveAsync(registry);

            var collisionName = SaveSlotService.NewParkedFolderName(0, "new");
            var collisionPath = Path.Combine(env.Context.ServerPaths.SaveRoot, collisionName);
            Directory.CreateDirectory(collisionPath);
            await ThrowsAsync<IOException>(() => env.Slots.RenameTagAsync(0, "new"));

            var restored = await env.Slots.LoadAsync();
            Equal("old", restored.Slots.Single().Tag);
            Equal(SaveSlotService.NewParkedFolderName(0, "old"), restored.Slots.Single().ParkedFolder);
            True(Directory.Exists(collisionPath), "rename collision path was modified");
        });
    }

    private static async Task JournalCompletionRemovesAuthorityLastAsync()
    {
        await InTempAsync(async root =>
        {
            var env = CreateEnvironment(root);
            var journal = new OperationJournalService(env.Context, env.Files);
            var operation = new PendingOperation
            {
                Type = "Synthetic",
                FromSlot = 0,
                FromWorldUid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ToSlot = 1,
                ToWorldUid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
            };
            await journal.WriteAsync(operation, "Prepared");
            await journal.WriteAsync(operation, "Verified");
            True(File.Exists(env.Context.StatePaths.PendingOperationPath), "current journal authority missing before completion");
            True(File.Exists(env.Context.StatePaths.PendingOperationPath + ".previous"), "previous journal copy missing before completion");
            journal.Complete();
            True(!File.Exists(env.Context.StatePaths.PendingOperationPath), "current journal authority survived completion");
            True(!File.Exists(env.Context.StatePaths.PendingOperationPath + ".previous"), "previous journal copy survived completion");
        });
    }

    private static async Task FirstAttachCleansPreservedEmptyStateAsync()
    {
        await InTempAsync(async root =>
        {
            var serverRoot = Path.Combine(root, "PalServer");
            var installRoot = Path.Combine(root, "App");
            var localStateRoot = Path.Combine(root, "LocalState");
            Directory.CreateDirectory(serverRoot);
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(localStateRoot);
            File.WriteAllBytes(Path.Combine(serverRoot, "PalServer.exe"), [0]);

            var server = RegisteredServer.Create("synthetic server", serverRoot);
            var app = AppPaths.ForExplicitRoots(installRoot, localStateRoot);
            var context = PalContext.ForRegisteredServer(app, server);
            const string uid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            CreateWorld(Path.Combine(context.ServerPaths.ActivePath, uid));
            Directory.CreateDirectory(context.ServerPaths.ConfigRoot);
            await File.WriteAllTextAsync(context.ServerPaths.SettingsPath, RenderProfileIni(CreateValidProfileValues()));

            Directory.CreateDirectory(context.StatePaths.StateRoot);
            var files = new SafeFileService();
            await files.WriteJsonAsync(Path.Combine(context.StatePaths.StateRoot, "StateActivation.json"), new StateActivationRecord
            {
                ServerId = server.Id,
                Mode = "EmptyNoWorld"
            });

            var state = new ServerStateService(app, files);
            Equal(ServerStateKind.EmptyNoWorld, (await state.AssessAsync(server)).Kind);
            await state.AttachAsync(server);
            Equal(ServerStateKind.Ready, (await state.AssessAsync(server)).Kind);

            var preservedPrefix = Path.GetFileName(context.StatePaths.StateRoot) + ".empty-before-attach-";
            var leftovers = Directory.EnumerateDirectories(app.ServersStateRoot)
                .Where(path => Path.GetFileName(path).StartsWith(preservedPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Equal(0, leftovers.Length);
        });
    }

    private static Dictionary<string, string> CreateValidProfileValues() =>
        WorldSettingsService.ProfileDefinitions.ToDictionary(
            definition => definition.Key,
            definition => definition.Kind switch
            {
                WorldSettingKind.Boolean => "False",
                WorldSettingKind.Choice => definition.Choices![0],
                WorldSettingKind.Integer => Math.Ceiling(definition.Minimum).ToString(CultureInfo.InvariantCulture),
                WorldSettingKind.Number => Math.Max(definition.Minimum, Math.Min(definition.Maximum, definition.Minimum == 0 ? 1 : definition.Minimum)).ToString(CultureInfo.InvariantCulture),
                _ => ""
            },
            StringComparer.OrdinalIgnoreCase);

    private static string RenderProfileIni(IReadOnlyDictionary<string, string> values) =>
        "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(" +
        string.Join(',', WorldSettingsService.ProfileDefinitions.Select(definition => $"{definition.Key}={values[definition.Key]}")) + ")";

    private static TestEnvironment CreateEnvironment(string root)
    {
        var server = Path.Combine(root, "PalServer");
        var install = Path.Combine(root, "App");
        var state = Path.Combine(root, "State");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(state);
        File.WriteAllBytes(Path.Combine(server, "PalServer.exe"), [0]);
        Directory.CreateDirectory(Path.Combine(server, "Pal", "Saved"));
        var registered = RegisteredServer.Create("synthetic server", server);
        var context = PalContext.ForRegisteredServer(new AppPaths(install, state), registered);
        var files = new SafeFileService();
        var log = new LoggingService(context);
        var processes = new ServerProcessService(context, log);
        var slots = new SaveSlotService(context, processes, log, files);
        return new TestEnvironment(context, files, slots);
    }

    private static void CreateWorld(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "Players"));
        File.WriteAllText(Path.Combine(path, "Level.sav"), "synthetic-world");
        File.WriteAllText(Path.Combine(path, "LevelMeta.sav"), "synthetic-meta");
    }

    private static async Task InTempAsync(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "PalServerManager-P2P3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { await body(path); }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
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

    private sealed record TestEnvironment(PalContext Context, SafeFileService Files, SaveSlotService Slots);
}
