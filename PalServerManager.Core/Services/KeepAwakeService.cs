using System.Diagnostics;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class KeepAwakeService(PalContext context, ServerProcessService processes, LoggingService log)
{
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private readonly object _watcherSync = new();
    private Task? _watcher;
    private int _boundPid;

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCurrentBindingAsync(cancellationToken);
        lock (_watcherSync)
        {
            if (_watcher is null || _watcher.IsCompleted)
                _watcher = WatchServerProcessAsync();
        }
    }

    private async Task EnsureCurrentBindingAsync(CancellationToken cancellationToken)
    {
        await _bindingGate.WaitAsync(cancellationToken);
        try
        {
            var server = processes.GetSnapshot();
            if (!server.IsRunning)
            {
                Volatile.Write(ref _boundPid, 0);
                return;
            }

            var nativePath = context.AppPaths.KeepAwakeExecutable;
            if (!File.Exists(nativePath))
                throw new FileNotFoundException("缺少原生防睡眠辅助程序 PalServer-KeepAwake.exe。", nativePath);
            var preferredPid = SelectServerPid(server);
            if (Volatile.Read(ref _boundPid) == preferredPid) return;
            try
            {
                using var helper = Process.Start(new ProcessStartInfo(nativePath, $"--pid {preferredPid}")
                {
                    WorkingDirectory = context.AppPaths.InstallRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (helper is null) throw new InvalidOperationException("无法创建防睡眠辅助进程。");
                var exitedEarly = await Task.Run(() => helper.WaitForExit(750), cancellationToken);
                if (exitedEarly && helper.ExitCode != 0)
                    throw new InvalidOperationException($"原生防睡眠辅助程序启动失败，退出代码 {helper.ExitCode}。");
                Volatile.Write(ref _boundPid, preferredPid);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await log.WriteAsync($"Native keep-awake helper could not start for PalServer PID {preferredPid}: {exception.Message}", cancellationToken);
                throw;
            }
        }
        finally
        {
            _bindingGate.Release();
        }
    }

    private async Task WatchServerProcessAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var server = processes.GetSnapshot();
                if (!server.IsRunning)
                {
                    Volatile.Write(ref _boundPid, 0);
                    return;
                }
                var preferredPid = SelectServerPid(server);
                if (Volatile.Read(ref _boundPid) == preferredPid) continue;
                await EnsureCurrentBindingAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                try { await log.WriteAsync($"Native keep-awake rebind check failed: {exception.Message}", CancellationToken.None); }
                catch { }
            }
        }
    }

    private int SelectServerPid(ServerProcessSnapshot snapshot)
    {
        var preferredPaths = new[]
        {
            context.ServerPaths.ShippingExe,
            context.ServerPaths.ShippingCmdExe,
            context.ServerPaths.ServerExe
        }.Select(Path.GetFullPath).ToArray();

        foreach (var preferredPath in preferredPaths)
        {
            foreach (var pid in snapshot.ProcessIds)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var processPath = process.MainModule?.FileName;
                    if (processPath is not null &&
                        string.Equals(Path.GetFullPath(processPath), preferredPath, StringComparison.OrdinalIgnoreCase))
                        return pid;
                }
                catch { }
            }
        }

        return snapshot.ProcessIds.First();
    }
}
