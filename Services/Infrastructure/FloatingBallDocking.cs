namespace Timenotifier.Services.Infrastructure;

public enum FloatingBallState
{
    Expanded,
    Docked
}

public enum DockEdge
{
    Left,
    Right,
    Top,
    Bottom
}

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

public readonly record struct WindowSize(double Width, double Height);

public readonly record struct WorkAreaBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

public readonly record struct DockResult(
    FloatingBallState State,
    DockEdge? Edge,
    double Left,
    double Top,
    double Width,
    double Height);

public static class FloatingBallDocking
{
    public const double PeekSize = 24;
    public const double SnapThreshold = 24;

    public static DockResult SnapOrClamp(WindowBounds window, WorkAreaBounds workArea)
    {
        var edge = GetNearestEdgeWithinThreshold(window, workArea);
        if (edge is null)
        {
            return new DockResult(
                FloatingBallState.Expanded,
                null,
                Clamp(window.Left, workArea.Left, workArea.Right - window.Width),
                Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height),
                window.Width,
                window.Height);
        }

        var (left, top) = GetDockedPosition(window, workArea, edge.Value);
        var (width, height) = GetDockedSize(window, edge.Value);
        return new DockResult(FloatingBallState.Docked, edge, left, top, width, height);
    }

    public static DockResult DockToEdge(WindowBounds window, WorkAreaBounds workArea, DockEdge edge)
    {
        var (left, top) = GetDockedPosition(window, workArea, edge);
        var (width, height) = GetDockedSize(window, edge);
        return new DockResult(FloatingBallState.Docked, edge, left, top, width, height);
    }

    public static DockResult HideToRightEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        return new DockResult(
            FloatingBallState.Docked,
            DockEdge.Right,
            workArea.Right - PeekSize,
            Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height),
            PeekSize,
            window.Height);
    }

    public static DockResult ExpandFromDock(WindowBounds window, WorkAreaBounds workArea, WindowSize expandedSize, DockEdge edge)
    {
        var left = edge switch
        {
            DockEdge.Left => workArea.Left,
            DockEdge.Right => workArea.Right - expandedSize.Width,
            _ => Clamp(window.Left, workArea.Left, workArea.Right - expandedSize.Width)
        };

        var top = edge switch
        {
            DockEdge.Top => workArea.Top,
            DockEdge.Bottom => workArea.Bottom - expandedSize.Height,
            _ => Clamp(window.Top, workArea.Top, workArea.Bottom - expandedSize.Height)
        };

        return new DockResult(FloatingBallState.Expanded, null, left, top, expandedSize.Width, expandedSize.Height);
    }

    public static DockEdge GetNearestEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        var distances = new[]
        {
            (Edge: DockEdge.Left, Distance: Math.Abs(window.Left - workArea.Left)),
            (Edge: DockEdge.Right, Distance: Math.Abs(workArea.Right - (window.Left + window.Width))),
            (Edge: DockEdge.Top, Distance: Math.Abs(window.Top - workArea.Top)),
            (Edge: DockEdge.Bottom, Distance: Math.Abs(workArea.Bottom - (window.Top + window.Height)))
        };

        return distances.OrderBy(item => item.Distance).First().Edge;
    }

    public static WindowBounds GetPopupAnchor(
        WindowBounds window,
        WorkAreaBounds workArea,
        WindowSize expandedSize,
        FloatingBallState state,
        DockEdge? edge)
    {
        if (state != FloatingBallState.Docked || edge is null)
        {
            return window;
        }

        var expanded = ExpandFromDock(window, workArea, expandedSize, edge.Value);
        return new WindowBounds(expanded.Left, expanded.Top, expanded.Width, expanded.Height);
    }

    private static DockEdge? GetNearestEdgeWithinThreshold(WindowBounds window, WorkAreaBounds workArea)
    {
        var distances = new[]
        {
            (Edge: DockEdge.Left, Distance: DistanceToLeftEdge(window, workArea)),
            (Edge: DockEdge.Right, Distance: DistanceToRightEdge(window, workArea)),
            (Edge: DockEdge.Top, Distance: DistanceToTopEdge(window, workArea)),
            (Edge: DockEdge.Bottom, Distance: DistanceToBottomEdge(window, workArea))
        };

        var nearest = distances.OrderBy(item => item.Distance).First();
        return nearest.Distance <= SnapThreshold ? nearest.Edge : null;
    }

    private static (double Left, double Top) GetDockedPosition(WindowBounds window, WorkAreaBounds workArea, DockEdge edge)
    {
        return edge switch
        {
            DockEdge.Left => (
                workArea.Left,
                Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height)),
            DockEdge.Right => (
                workArea.Right - PeekSize,
                Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height)),
            DockEdge.Top => (
                Clamp(window.Left, workArea.Left, workArea.Right - window.Width),
                workArea.Top),
            DockEdge.Bottom => (
                Clamp(window.Left, workArea.Left, workArea.Right - window.Width),
                workArea.Bottom - PeekSize),
            _ => (window.Left, window.Top)
        };
    }

    private static (double Width, double Height) GetDockedSize(WindowBounds window, DockEdge edge)
    {
        return edge is DockEdge.Left or DockEdge.Right
            ? (PeekSize, window.Height)
            : (window.Width, PeekSize);
    }

    private static double DistanceToLeftEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        return window.Left <= workArea.Left ? 0 : window.Left - workArea.Left;
    }

    private static double DistanceToRightEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        var windowRight = window.Left + window.Width;
        return windowRight >= workArea.Right ? 0 : workArea.Right - windowRight;
    }

    private static double DistanceToTopEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        return window.Top <= workArea.Top ? 0 : window.Top - workArea.Top;
    }

    private static double DistanceToBottomEdge(WindowBounds window, WorkAreaBounds workArea)
    {
        var windowBottom = window.Top + window.Height;
        return windowBottom >= workArea.Bottom ? 0 : workArea.Bottom - windowBottom;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }
}
