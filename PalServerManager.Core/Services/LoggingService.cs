using System.Text;
using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class LoggingService(PalContext context)
{
    private const long RotateAtBytes = 5 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var mutex = new Mutex(false, @"Local\PalServerAdminLog-v1");
            var ownsMutex = false;
            try
            {
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) return;

                Directory.CreateDirectory(context.StatePaths.LogRoot);
                RotateIfNeeded();
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(context.StatePaths.LogPath, line, new UTF8Encoding(true), cancellationToken);
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Logging must never interrupt a server operation.
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(context.StatePaths.LogPath) || new FileInfo(context.StatePaths.LogPath).Length < RotateAtBytes) return;
        var oldest = context.StatePaths.LogPath + ".3";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = 2; index >= 1; index--)
        {
            var source = context.StatePaths.LogPath + "." + index;
            if (File.Exists(source)) File.Move(source, context.StatePaths.LogPath + "." + (index + 1), true);
        }
        File.Move(context.StatePaths.LogPath, context.StatePaths.LogPath + ".1", true);
    }
}
