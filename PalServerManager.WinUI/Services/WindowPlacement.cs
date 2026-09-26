using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace HaoHaoTianTian.PalHR.Services;

internal static class WindowPlacement
{
    public static void CenterOnPrimaryDisplay(AppWindow appWindow, WindowId windowId, int width, int height)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
    }
}
