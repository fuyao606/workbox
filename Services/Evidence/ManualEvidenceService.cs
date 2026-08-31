using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Evidence;

public sealed class ManualEvidenceService(
    IEvidenceStore evidenceStore,
    ITimeProvider timeProvider,
    IActiveWindowService activeWindowService)
{
    public async Task<EvidenceItem> SaveNoteAsync(
        string content,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("记录内容不能为空。");
        }

        var now = timeProvider.Now;
        var activeWindow = activeWindowService.GetActiveWindow();
        var evidence = new EvidenceItem
        {
            Id = $"manual:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            Kind = EvidenceKinds.ManualNote,
            CapturedAt = now,
            Title = $"手动记录 {now:HH:mm:ss}",
            Summary = content.Trim(),
            Tags = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            WindowTitle = activeWindow.Title,
            ProcessName = activeWindow.ProcessName,
            Included = true
        };

        await evidenceStore.UpsertEvidenceAsync([evidence], cancellationToken);
        return evidence;
    }
}
