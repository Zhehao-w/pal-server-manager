using System.Diagnostics;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class KeepAwakeService(PalContext context, ServerProcessService processes, LoggingService log)
{
    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        var server = processes.GetSnapshot();
        if (!server.IsRunning) return;

        var nativePath = context.AppPaths.KeepAwakeExecutable;
        if (!File.Exists(nativePath))
            throw new FileNotFoundException("缺少原生防睡眠辅助程序 PalServer-KeepAwake.exe。", nativePath);
        if (IsExactHelperRunning(nativePath)) return;
        var preferredPid = server.ProcessIds.First();
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
            if (!exitedEarly || helper.ExitCode == 0) return;
            throw new InvalidOperationException($"原生防睡眠辅助程序启动失败，退出代码 {helper.ExitCode}。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await log.WriteAsync($"Native keep-awake helper could not start: {exception.Message}", cancellationToken);
            throw;
        }
    }

    private static bool IsExactHelperRunning(string expectedPath)
    {
        foreach (var process in Process.GetProcessesByName("PalServer-KeepAwake"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }
}
