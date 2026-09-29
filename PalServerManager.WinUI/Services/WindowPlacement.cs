using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace HaoHaoTianTian.PalHR.Services;

internal static class WindowPlacement
{
    private const double DefaultDpi = 96.0;

    public static int EffectivePixelsToPhysical(WindowId windowId, int effectivePixels)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(windowId);
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / DefaultDpi : 1.0;
        return Math.Max(1, (int)Math.Round(effectivePixels * scale, MidpointRounding.AwayFromZero));
    }

    public static void CenterOnPrimaryDisplay(AppWindow appWindow, WindowId windowId, int width, int height)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var physicalWidth = Math.Min(EffectivePixelsToPhysical(windowId, width), workArea.Width);
        var physicalHeight = Math.Min(EffectivePixelsToPhysical(windowId, height), workArea.Height);
        var x = workArea.X + Math.Max(0, (workArea.Width - physicalWidth) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - physicalHeight) / 2);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, physicalWidth, physicalHeight));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
