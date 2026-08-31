namespace Timenotifier.Services.Infrastructure;

public interface ITimeProvider
{
    DateTime Now { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public static SystemTimeProvider Instance { get; } = new();

    private SystemTimeProvider()
    {
    }

    public DateTime Now => DateTime.Now;
}
