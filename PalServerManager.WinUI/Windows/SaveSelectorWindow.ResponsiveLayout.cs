using HaoHaoTianTian.PalHR.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR.Windows;

public sealed partial class SaveSelectorWindow
{
    private const double ConstrainedWindowHeight = 620;
    private const double ConstrainedContentHeight = 572;
    private ScrollViewer? _contentScroller;
    private bool _responsiveLayoutInitialized;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_responsiveLayoutInitialized) return;
        _responsiveLayoutInitialized = true;

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 900), workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 620), workArea.Height);
        }

        RootGrid.SizeChanged += (_, args) => UpdateConstrainedLayout(args.NewSize.Height);
        UpdateConstrainedLayout(RootGrid.ActualHeight);
    }

    private void UpdateConstrainedLayout(double windowHeight)
    {
        var constrained = windowHeight > 0 && windowHeight < ConstrainedWindowHeight;

        if (constrained && _contentScroller is null)
        {
            RootGrid.Children.Remove(ContentGrid);
            ContentGrid.Height = ConstrainedContentHeight;
            _contentScroller = new ScrollViewer
            {
                Content = ContentGrid,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(_contentScroller, 1);
            RootGrid.Children.Add(_contentScroller);
            return;
        }

        if (!constrained && _contentScroller is not null)
        {
            _contentScroller.Content = null;
            RootGrid.Children.Remove(_contentScroller);
            _contentScroller = null;
            ContentGrid.Height = double.NaN;
            Grid.SetRow(ContentGrid, 1);
            RootGrid.Children.Add(ContentGrid);
        }
    }
}
