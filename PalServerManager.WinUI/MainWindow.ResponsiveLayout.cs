using System.Linq;
using HaoHaoTianTian.PalHR.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace HaoHaoTianTian.PalHR;

public sealed partial class MainWindow
{
    private const double ConstrainedWindowHeight = 734;
    private const double ConstrainedDashboardHeight = 680;
    private Grid? _dashboardGrid;
    private ScrollViewer? _dashboardScroller;
    private bool _responsiveLayoutInitialized;
    private double _lastRasterizationScale;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_responsiveLayoutInitialized) return;
        _responsiveLayoutInitialized = true;

        _dashboardGrid = RootGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1);
        if (_dashboardGrid is null) return;

        var xamlRoot = RootGrid.XamlRoot;
        _lastRasterizationScale = xamlRoot.RasterizationScale;
        xamlRoot.Changed += (_, _) =>
        {
            var scale = xamlRoot.RasterizationScale;
            if (Math.Abs(scale - _lastRasterizationScale) < 0.001) return;
            _lastRasterizationScale = scale;
            ApplyResponsiveMinimum();
        };
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange) ApplyResponsiveMinimum();
        };

        ApplyResponsiveMinimum();
        RootGrid.SizeChanged += (_, args) => UpdateConstrainedLayout(args.NewSize.Height);
        UpdateConstrainedLayout(RootGrid.ActualHeight);
    }

    private void ApplyResponsiveMinimum()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        if (_appWindow.Presenter is not OverlappedPresenter presenter) return;

        presenter.PreferredMinimumWidth = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 1000), workArea.Width);
        presenter.PreferredMinimumHeight = Math.Min(WindowPlacement.EffectivePixelsToPhysical(windowId, 740), workArea.Height);
    }

    private void UpdateConstrainedLayout(double windowHeight)
    {
        if (_dashboardGrid is null) return;
        var constrained = windowHeight > 0 && windowHeight < ConstrainedWindowHeight;

        if (constrained && _dashboardScroller is null)
        {
            RootGrid.Children.Remove(_dashboardGrid);
            _dashboardGrid.Height = ConstrainedDashboardHeight;
            _dashboardScroller = new ScrollViewer
            {
                Content = _dashboardGrid,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(_dashboardScroller, 1);
            RootGrid.Children.Add(_dashboardScroller);
            return;
        }

        if (!constrained && _dashboardScroller is not null)
        {
            _dashboardScroller.Content = null;
            RootGrid.Children.Remove(_dashboardScroller);
            _dashboardScroller = null;
            _dashboardGrid.Height = double.NaN;
            Grid.SetRow(_dashboardGrid, 1);
            RootGrid.Children.Add(_dashboardGrid);
        }
    }
}
