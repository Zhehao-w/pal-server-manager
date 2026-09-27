using System.Text.Json;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class ServerRegistryService(AppPaths app, SafeFileService files)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ServerRegistryDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(app.ServerRegistryPath)) return new ServerRegistryDocument();
        try
        {
            await using var stream = File.OpenRead(app.ServerRegistryPath);
            var document = await JsonSerializer.DeserializeAsync<ServerRegistryDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("Servers.json is empty.");
            Validate(document);
            return document;
        }
        catch (JsonException error) { throw new InvalidOperationException($"Servers.json is malformed: {error.Message}", error); }
    }

    public async Task<RegisteredServer> RegisterFromExeAsync(string serverExe, string displayName, CancellationToken cancellationToken = default)
    {
        var root = ValidateSelectedExe(serverExe);
        var name = ValidateDisplayName(displayName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            if (document.Servers.Any(server => PathsEqual(server.ServerRoot, root)))
                throw new InvalidOperationException("This PalServer installation is already registered. Select its existing registration.");
            var original = Copy(document);
            var registered = RegisteredServer.Create(name, root);
            document.Servers.Add(registered);
            document.SelectedServerId = registered.Id;
            try
            {
                await SaveAsync(document, cancellationToken);
                Directory.CreateDirectory(ServerStatePaths.ForRegisteredServer(app, registered).StateRoot);
                return registered;
            }
            catch (Exception registrationError)
            {
                try
                {
                    var persisted = await LoadAsync(CancellationToken.None);
                    if (persisted.Servers.Any(server => server.Id == registered.Id))
                        await SaveAsync(original, CancellationToken.None);
                    var restored = await LoadAsync(CancellationToken.None);
                    if (!LogicallyEqual(original, restored))
                        throw new InvalidOperationException("The registry does not match its pre-registration state.");
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        "Server registration failed and registry rollback could not be verified.",
                        new AggregateException(registrationError, rollbackError));
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<RegisteredServer> RelocateAsync(string id, string serverExe, CancellationToken cancellationToken = default)
    {
        var root = ValidateSelectedExe(serverExe);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var index = document.Servers.FindIndex(server => server.Id == id);
            if (index < 0) throw new InvalidOperationException("Server registration not found.");
            if (document.Servers.Any(server => server.Id != id && PathsEqual(server.ServerRoot, root)))
                throw new InvalidOperationException("That PalServer path belongs to a different registration.");
            var relocated = document.Servers[index] with { ServerRoot = root };
            document.Servers[index] = relocated;
            await SaveAsync(document, cancellationToken);
            return relocated;
        }
        finally { _gate.Release(); }
    }

    public async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            if (!document.Servers.Any(server => server.Id == id)) throw new InvalidOperationException("Server registration not found.");
            document.SelectedServerId = id;
            await SaveAsync(document, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveRegistrationAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            if (document.Servers.RemoveAll(server => server.Id == id) == 0) return;
            if (document.SelectedServerId == id) document.SelectedServerId = document.Servers.FirstOrDefault()?.Id;
            await SaveAsync(document, cancellationToken);
            // Intentionally do not delete Servers/<id>: registration removal is reversible.
        }
        finally { _gate.Release(); }
    }

    public static string ValidateSelectedExe(string path)
    {
        if (!string.Equals(Path.GetFileName(path), "PalServer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select PalServer.exe, not another executable.");
        var root = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException("Invalid PalServer.exe path.");
        if (!ServerRootService.IsValidRoot(root)) throw new InvalidOperationException("The selected PalServer installation is incomplete or missing.");
        return root;
    }

    private static string ValidateDisplayName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 80) throw new InvalidOperationException("Display name must be 1–80 characters.");
        return name;
    }

    private Task SaveAsync(ServerRegistryDocument document, CancellationToken cancellationToken)
    {
        Validate(document);
        return files.WriteJsonAsync(app.ServerRegistryPath, document, JsonOptions, keepPrevious: true, cancellationToken: cancellationToken);
    }

    private static ServerRegistryDocument Copy(ServerRegistryDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        SelectedServerId = document.SelectedServerId,
        Servers = [.. document.Servers]
    };

    private static bool LogicallyEqual(ServerRegistryDocument expected, ServerRegistryDocument actual) =>
        expected.SchemaVersion == actual.SchemaVersion &&
        expected.SelectedServerId == actual.SelectedServerId &&
        expected.Servers.SequenceEqual(actual.Servers);

    private static void Validate(ServerRegistryDocument document)
    {
        if (document.SchemaVersion != 1 || document.Servers is null) throw new InvalidOperationException("Unsupported Servers.json schema.");
        if (document.Servers.GroupBy(server => server.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Duplicate server IDs in Servers.json.");
        foreach (var server in document.Servers)
        {
            if (!Guid.TryParseExact(server.Id, "N", out _)) throw new InvalidOperationException("Invalid server ID in Servers.json.");
            ValidateDisplayName(server.DisplayName);
            if (string.IsNullOrWhiteSpace(server.ServerRoot) || !Path.IsPathFullyQualified(server.ServerRoot))
                throw new InvalidOperationException("Server root must be an absolute path.");
        }
        if (document.Servers.GroupBy(server => Path.GetFullPath(server.ServerRoot), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Duplicate PalServer roots in Servers.json.");
        if (document.SelectedServerId is not null && !document.Servers.Any(server => server.Id == document.SelectedServerId))
            throw new InvalidOperationException("Selected server ID does not exist.");
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
