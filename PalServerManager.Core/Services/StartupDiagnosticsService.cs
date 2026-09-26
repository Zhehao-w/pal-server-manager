using System.Runtime.InteropServices;
using System.Text;
using HaoHaoTianTian.PalHR.Models;
using Microsoft.Win32.SafeHandles;

namespace HaoHaoTianTian.PalHR.Services;

public static class StartupDiagnosticsService
{
    private static readonly object Gate = new();

    public static string Describe(AppPaths app, RegisteredServer? selected = null)
    {
        var state = selected is null ? "(none)" : ServerStatePaths.ForRegisteredServer(app, selected).StateRoot;
        return $"Executable={Environment.ProcessPath ?? "(unknown)"}; " +
               $"InstallRoot={app.InstallRoot}; LocalStateRoot={app.LocalStateRoot}; " +
               $"ServersJson={app.ServerRegistryPath}; " +
               $"PhysicalServersJson={ResolvePhysicalFile(app.ServerRegistryPath)}; " +
               $"SelectedServerId={selected?.Id ?? "(none)"}; " +
               $"ServerRoot={selected?.ServerRoot ?? "(none)"}; ServerStateRoot={state}";
    }

    public static void Write(AppPaths app, RegisteredServer? selected = null)
    {
        var line = $"[{DateTimeOffset.Now:O}] {Describe(app, selected)}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(app.AppLogRoot);
                File.AppendAllText(Path.Combine(app.AppLogRoot, "Startup.log"), line, new UTF8Encoding(false));
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Startup diagnostics could not be written: {error}");
        }
    }

    public static string ResolvePhysicalFile(string path)
    {
        if (!File.Exists(path)) return "(not present)";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            return count is > 0 and < 32768 ? buffer.ToString() : "(resolution failed)";
        }
        catch (Exception error)
        {
            return $"(resolution failed: {error.GetType().Name})";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
