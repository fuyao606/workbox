using Xunit;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Tests;

public sealed class FloatingBallDockingTests
{
    private static readonly WorkAreaBounds WorkArea = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(4, 200, DockEdge.Left, 0, 200)]
    [InlineData(1756, 200, DockEdge.Right, 1896, 200)]
    [InlineData(600, 8, DockEdge.Top, 600, 0)]
    [InlineData(600, 990, DockEdge.Bottom, 600, 1016)]
    public void Snaps_to_nearest_edge_when_within_threshold(
        double left,
        double top,
        DockEdge expectedEdge,
        double expectedLeft,
        double expectedTop)
    {
        var result = FloatingBallDocking.SnapOrClamp(new WindowBounds(left, top, 160, 44), WorkArea);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(expectedEdge, result.Edge);
        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
    }

    [Theory]
    [InlineData(-40, 200, DockEdge.Left, 0, 200)]
    [InlineData(1925, 200, DockEdge.Right, 1896, 200)]
    public void Snaps_when_window_crosses_side_edge(
        double left,
        double top,
        DockEdge expectedEdge,
        double expectedLeft,
        double expectedTop)
    {
        var result = FloatingBallDocking.SnapOrClamp(new WindowBounds(left, top, 160, 44), WorkArea);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(expectedEdge, result.Edge);
        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
        Assert.Equal(24, result.Width);
        Assert.Equal(44, result.Height);
    }

    [Fact]
    public void Keeps_expanded_and_clamps_when_not_near_edge()
    {
        var result = FloatingBallDocking.SnapOrClamp(new WindowBounds(900, 500, 160, 44), WorkArea);

        Assert.Equal(FloatingBallState.Expanded, result.State);
        Assert.Null(result.Edge);
        Assert.Equal(900, result.Left);
        Assert.Equal(500, result.Top);
    }

    [Fact]
    public void Expands_from_right_edge_inside_work_area()
    {
        var result = FloatingBallDocking.ExpandFromDock(
            new WindowBounds(1896, 200, 160, 44),
            WorkArea,
            new WindowSize(160, 44),
            DockEdge.Right);

        Assert.Equal(FloatingBallState.Expanded, result.State);
        Assert.Null(result.Edge);
        Assert.Equal(1760, result.Left);
        Assert.Equal(200, result.Top);
    }

    [Fact]
    public void Dock_to_right_edge_keeps_only_peek_visible()
    {
        var result = FloatingBallDocking.DockToEdge(
            new WindowBounds(1200, 120, 160, 44),
            WorkArea,
            DockEdge.Right);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(DockEdge.Right, result.Edge);
        Assert.Equal(1896, result.Left);
        Assert.Equal(120, result.Top);
        Assert.Equal(24, result.Width);
        Assert.Equal(44, result.Height);
    }

    [Fact]
    public void Popup_anchor_uses_expanded_position_without_changing_docked_position()
    {
        var anchor = FloatingBallDocking.GetPopupAnchor(
            new WindowBounds(1896, 120, 160, 44),
            WorkArea,
            new WindowSize(160, 44),
            FloatingBallState.Docked,
            DockEdge.Right);

        Assert.Equal(1760, anchor.Left);
        Assert.Equal(120, anchor.Top);
    }

    [Fact]
    public void Uses_supplied_work_area_for_multi_monitor_edges()
    {
        var secondMonitor = new WorkAreaBounds(1920, 0, 3840, 1040);

        var result = FloatingBallDocking.SnapOrClamp(new WindowBounds(3684, 80, 160, 44), secondMonitor);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(DockEdge.Right, result.Edge);
        Assert.Equal(3816, result.Left);
        Assert.Equal(80, result.Top);
    }

    [Fact]
    public void Hide_to_right_edge_keeps_icon_peek_visible()
    {
        var result = FloatingBallDocking.HideToRightEdge(
            new WindowBounds(900, 500, 160, 44),
            WorkArea);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(DockEdge.Right, result.Edge);
        Assert.Equal(1896, result.Left);
        Assert.Equal(500, result.Top);
        Assert.Equal(24, result.Width);
        Assert.Equal(44, result.Height);
    }

    [Fact]
    public void Hide_to_right_edge_uses_device_independent_work_area_after_dpi_conversion()
    {
        var deviceWorkArea = new WorkAreaBounds(0, 0, 1920, 1040);
        var dipWorkArea = ScreenCoordinateMapper.DevicePixelsToDips(deviceWorkArea, new CoordinateScale(1.5, 1.5));

        var result = FloatingBallDocking.HideToRightEdge(
            new WindowBounds(600, 300, 160, 44),
            dipWorkArea);

        Assert.Equal(FloatingBallState.Docked, result.State);
        Assert.Equal(DockEdge.Right, result.Edge);
        Assert.Equal(1256, result.Left);
        Assert.Equal(300, result.Top);
        Assert.Equal(24, result.Width);
        Assert.Equal(44, result.Height);
    }

    [Fact]
    public void Window_bounds_are_converted_to_device_pixels_before_screen_lookup()
    {
        var deviceBounds = ScreenCoordinateMapper.DipsToDevicePixels(
            new WindowBounds(600, 300, 160, 44),
            new CoordinateScale(1.5, 1.5));

        Assert.Equal(900, deviceBounds.Left);
        Assert.Equal(450, deviceBounds.Top);
        Assert.Equal(240, deviceBounds.Width);
        Assert.Equal(66, deviceBounds.Height);
    }
}
