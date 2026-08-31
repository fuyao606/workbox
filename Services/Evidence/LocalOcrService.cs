namespace Timenotifier.Services.Evidence;

public interface ILocalOcrService
{
    Task<string> TryExtractTextAsync(string imagePath, CancellationToken cancellationToken = default);
}

public sealed class LocalOcrService : ILocalOcrService
{
    public Task<string> TryExtractTextAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("");
    }
}
