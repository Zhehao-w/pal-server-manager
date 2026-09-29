using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaoHaoTianTian.PalHR;

public sealed partial class MainWindow
{
    private const double ConstrainedWindowHeight = 732;
    private const double ConstrainedDashboardHeight = 680;
    private Grid? _dashboardGrid;
    private ScrollViewer? _dashboardScroller;
    private bool _responsiveLayoutInitialized;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_responsiveLayoutInitialized) return;
        _responsiveLayoutInitialized = true;

        _dashboardGrid = RootGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1);
        if (_dashboardGrid is null) return;

        RootGrid.SizeChanged += (_, args) => UpdateConstrainedLayout(args.NewSize.Height);
        UpdateConstrainedLayout(RootGrid.ActualHeight);
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
