namespace HaoHaoTianTian.PalHR.Models;

// Installation, Palworld data and Manager state are intentionally independent.
public sealed record AppPaths(string InstallRoot, string LocalStateRoot, string? KeepAwakeOverride = null)
{
    public string AppLogRoot => Path.Combine(LocalStateRoot, "Logs");
    public string ServerRegistryPath => Path.Combine(LocalStateRoot, "Servers.json");
    public string ServersStateRoot => Path.Combine(LocalStateRoot, "Servers");
    public string KeepAwakeExecutable => KeepAwakeOverride ?? Path.Combine(InstallRoot, "Helpers", "PalServer-KeepAwake.exe");

    public static AppPaths ForCurrentInstall() => ForExplicitRoots(
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalServerManager"));

    // Tests and external diagnostics must pass both roots explicitly. Never infer
    // a production LocalAppData path from a Work/Codex host process.
    public static AppPaths ForExplicitRoots(string installRoot, string localStateRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(localStateRoot) ||
            !Path.IsPathFullyQualified(installRoot) || !Path.IsPathFullyQualified(localStateRoot))
            throw new ArgumentException("InstallRoot and LocalStateRoot must be explicit absolute paths.");
        return new(Path.GetFullPath(installRoot), Path.GetFullPath(localStateRoot));
    }
}

public sealed record RegisteredServer(string Id, string DisplayName, string ServerRoot)
{
    public static RegisteredServer Create(string displayName, string serverRoot) =>
        new(Guid.NewGuid().ToString("N"), displayName, Path.GetFullPath(serverRoot));
}

public sealed record ServerPaths(string ServerRoot)
{
    public string ServerExe => Path.Combine(ServerRoot, "PalServer.exe");
    public string SavedRoot => Path.Combine(ServerRoot, "Pal", "Saved");
    public string SaveRoot => Path.Combine(SavedRoot, "SaveGames");
    public string ActivePath => Path.Combine(SaveRoot, "0");
    public string ConfigRoot => Path.Combine(SavedRoot, "Config", "WindowsServer");
    public string SettingsPath => Path.Combine(ConfigRoot, "PalWorldSettings.ini");
    public string UserSettingsPath => Path.Combine(ConfigRoot, "GameUserSettings.ini");
    public string DefaultWorldSettingsPath => Path.Combine(ServerRoot, "DefaultPalWorldSettings.ini");
    public string ShippingCmdExe => Path.Combine(ServerRoot, "Pal", "Binaries", "Win64", "PalServer-Win64-Shipping-Cmd.exe");
    public string ShippingExe => Path.Combine(ServerRoot, "Pal", "Binaries", "Win64", "PalServer-Win64-Shipping.exe");
    public string ParkedPath(string folderName) => Path.Combine(SaveRoot, folderName);
    public string BuiltInBackupRoot(string worldPath) => Path.Combine(worldPath, "backup", "world");
}

public sealed record ServerStatePaths(string StateRoot)
{
    public string RegistryPath => Path.Combine(StateRoot, "SaveSlots.json");
    public string ManagerSettingsPath => Path.Combine(StateRoot, "ManagerSettings.json");
    public string PlayerActivityPath => Path.Combine(StateRoot, "PlayerActivity.json");
    public string WorldSettingsRoot => Path.Combine(StateRoot, "WorldSettings");
    // Persisted filename is retained so existing profiles remain readable.
    public string WorldSettingsMarkerPath => Path.Combine(WorldSettingsRoot, "migration.json");
    public string NewWorldDefaultsPath => Path.Combine(WorldSettingsRoot, "new-world-defaults.json");
    public string LogRoot => Path.Combine(StateRoot, "Logs");
    public string LogPath => Path.Combine(LogRoot, "PalServerAdmin.log");
    public string BackupRoot => Path.Combine(StateRoot, "SaveBackups");
    public string PendingOperationPath => Path.Combine(StateRoot, "PendingOperation.json");
    public string BuildStatePath => Path.Combine(StateRoot, "WorldBuildState.json");
    public string WorldProfilePath(int slotId) => Path.Combine(WorldSettingsRoot, $"slot-{slotId:D3}.json");
    public string WorldBackupRoot(string worldUid) => Path.Combine(BackupRoot, worldUid);

    public static ServerStatePaths ForRegisteredServer(AppPaths app, RegisteredServer server)
    {
        if (!Guid.TryParseExact(server.Id, "N", out _)) throw new ArgumentException("Server ID must be a stable GUID in N format.", nameof(server));
        return new(Path.Combine(app.ServersStateRoot, server.Id));
    }
}

public sealed record PalContext(AppPaths AppPaths, ServerPaths ServerPaths, ServerStatePaths StatePaths)
{
    public static PalContext ForRegisteredServer(AppPaths app, RegisteredServer server) =>
        new(app, new ServerPaths(Path.GetFullPath(server.ServerRoot)), ServerStatePaths.ForRegisteredServer(app, server));
}
