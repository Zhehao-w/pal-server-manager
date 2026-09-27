using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

// Keeps first-run registration and attachment one logical operation for callers.
public sealed class FirstRunServerSetupService(ServerRegistryService registry, ServerStateService serverState)
{
    public async Task<RegisteredServer> RegisterAndAttachAsync(string serverExe, string displayName,
        CancellationToken cancellationToken = default)
    {
        var registered = await registry.RegisterFromExeAsync(serverExe, displayName, cancellationToken);
        try
        {
            await serverState.AttachAsync(registered, cancellationToken);
            return registered;
        }
        catch
        {
            // Attachment stages Manager state transactionally. Remove only the new registry entry so a
            // failed first run remains a pending selection rather than a misleading registered server.
            await registry.RemoveRegistrationAsync(registered.Id, CancellationToken.None);
            throw;
        }
    }
}
