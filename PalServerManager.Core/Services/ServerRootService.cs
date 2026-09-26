using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class ServerRootService
{
    // A registered path is necessary but not sufficient: startup also validates
    // the independently stored Manager state before any server operation.
    public PalContext CreateRegisteredContext(AppPaths app, RegisteredServer server)
    {
        if (!IsValidRoot(server.ServerRoot)) throw new InvalidOperationException("Registered PalServer root is invalid.");
        return PalContext.ForRegisteredServer(app, server);
    }

    public static bool IsValidRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(Path.Combine(full, "PalServer.exe")) && Directory.Exists(Path.Combine(full, "Pal", "Saved"));
        }
        catch
        {
            return false;
        }
    }

}
