using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class ServerDiscoveryService
{
    public IReadOnlyList<string> AutoDetect()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName);
        foreach (var drive in drives)
        {
            foreach (var relative in new[]
            {
                @"Program Files (x86)\Steam\steamapps\common\PalServer",
                @"Program Files\Steam\steamapps\common\PalServer",
                @"SteamLibrary\steamapps\common\PalServer",
                @"Steam\steamapps\common\PalServer",
                @"steamapps\common\PalServer"
            })
            {
                var candidate = Path.Combine(drive, relative);
                if (ServerRootService.IsValidRoot(candidate)) roots.Add(Path.GetFullPath(candidate));
            }
        }
        return roots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
