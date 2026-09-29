using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsContinuous = 0x80000000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SetThreadExecutionState(uint executionState);

    private static int Main(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--pid", StringComparison.OrdinalIgnoreCase) || !int.TryParse(args[1], out var pid) || pid <= 0) return 2;
        using var mutex = new Mutex(false, $@"Local\PalServerKeepAwakeNative-v2-{pid}");
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0, false); } catch (AbandonedMutexException) { owns = true; }
            if (!owns) return 0;
            Process server;
            try { server = Process.GetProcessById(pid); if (server.HasExited) { server.Dispose(); return 3; } } catch (ArgumentException) { return 3; }
            using (server)
            {
                if (SetThreadExecutionState(EsContinuous | EsSystemRequired) == 0) return 4;
                try { server.WaitForExit(); } finally { SetThreadExecutionState(EsContinuous); }
            }
            return 0;
        }
        finally { if (owns) try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
    }
}
