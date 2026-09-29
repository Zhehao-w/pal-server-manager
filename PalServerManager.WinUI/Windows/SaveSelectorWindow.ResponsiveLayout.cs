using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR.Windows;

public sealed partial class SaveSelectorWindow
{
    private bool _responsiveMinimumApplied;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_responsiveMinimumApplied) return;
        _responsiveMinimumApplied = true;

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        if (_appWindow.Presenter is not OverlappedPresenter presenter) return;

        presenter.PreferredMinimumWidth = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 900), workArea.Width);
        presenter.PreferredMinimumHeight = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 620), workArea.Height);
    }
}
