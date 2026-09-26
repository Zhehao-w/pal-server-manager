using System.Diagnostics;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class ServerProcessService(PalContext context, LoggingService log)
{
    private static readonly string[] ProcessNames = ["PalServer", "PalServer-Win64-Shipping-Cmd", "PalServer-Win64-Shipping"];

    public Task<ServerProcessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetSnapshot(), cancellationToken);

    public ServerProcessSnapshot GetSnapshot()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(context.ServerPaths.ServerExe),
            Path.GetFullPath(context.ServerPaths.ShippingCmdExe),
            Path.GetFullPath(context.ServerPaths.ShippingExe)
        };

        var matched = new List<Process>();
        try
        {
            foreach (var name in ProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is not null && allowed.Contains(Path.GetFullPath(path))) matched.Add(process);
                        else process.Dispose();
                    }
                    catch
                    {
                        process.Dispose();
                    }
                }
            }

            var ids = matched.Select(p => p.Id).Distinct().Order().ToArray();
            var cpu = matched.Sum(p => TryRead(() => p.TotalProcessorTime.TotalSeconds));
            var memory = matched.Sum(p => TryRead(() => (double)p.WorkingSet64)) / (1024d * 1024d);
            var starts = matched.Select(p => TryRead(() => new DateTimeOffset(p.StartTime))).Where(v => v is not null).Cast<DateTimeOffset>().ToArray();
            return new ServerProcessSnapshot(ids.Length > 0, ids, cpu, memory, starts.Length == 0 ? null : starts.Min());
        }
        finally
        {
            foreach (var process in matched) process.Dispose();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (GetSnapshot().IsRunning) throw new InvalidOperationException("PalServer 已经在运行。 ");
        var startInfo = new ProcessStartInfo(context.ServerPaths.ServerExe)
        {
            WorkingDirectory = context.ServerPaths.ServerRoot,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(startInfo)?.Dispose();
        await log.WriteAsync("Starting PalServer...", cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSnapshot().IsRunning) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("PalServer 进程在 30 秒内没有出现。 ");
    }

    public async Task<bool> ForceStopAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = GetSnapshot();
        if (!snapshot.IsRunning)
        {
            await log.WriteAsync("PalServer is not running; there is nothing to force-stop.", cancellationToken);
            return false;
        }

        await log.WriteAsync($"FORCE STOP requested. No save or graceful shutdown command will be sent. Process IDs: {string.Join(", ", snapshot.ProcessIds)}", cancellationToken);
        foreach (var id in snapshot.ProcessIds)
        {
            try { using var process = Process.GetProcessById(id); process.Kill(true); }
            catch (ArgumentException) { }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!GetSnapshot().IsRunning)
            {
                await log.WriteAsync("PalServer was force-stopped without saving.", cancellationToken);
                return true;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException("PalServer 进程在强制关服后仍未退出。 ");
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!GetSnapshot().IsRunning) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("PalServer 在限定时间内没有退出。 ");
    }

    private static double TryRead(Func<double> read)
    {
        try { return read(); } catch { return 0; }
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try { return read(); } catch { return null; }
    }
}
