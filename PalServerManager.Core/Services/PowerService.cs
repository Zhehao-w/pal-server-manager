using System.Diagnostics;

namespace HaoHaoTianTian.PalHR.Services;

public interface IPowerService
{
    void ScheduleShutdown();
}

public sealed class PowerService : IPowerService
{
    public void ScheduleShutdown()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var shutdown = Path.Combine(windows, "System32", "shutdown.exe");
        Process.Start(new ProcessStartInfo(shutdown)
        {
            ArgumentList = { "/s", "/t", "30", "/c", "Scheduled Palworld server shutdown completed." },
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })?.Dispose();
    }
}
