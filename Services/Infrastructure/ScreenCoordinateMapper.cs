namespace Timenotifier.Services.Infrastructure;

public readonly record struct CoordinateScale(double X, double Y);

public static class ScreenCoordinateMapper
{
    public static WindowBounds DipsToDevicePixels(WindowBounds bounds, CoordinateScale scale)
    {
        return new WindowBounds(
            bounds.Left * scale.X,
            bounds.Top * scale.Y,
            bounds.Width * scale.X,
            bounds.Height * scale.Y);
    }

    public static WorkAreaBounds DevicePixelsToDips(WorkAreaBounds workArea, CoordinateScale scale)
    {
        return new WorkAreaBounds(
            workArea.Left / scale.X,
            workArea.Top / scale.Y,
            workArea.Right / scale.X,
            workArea.Bottom / scale.Y);
    }
}
