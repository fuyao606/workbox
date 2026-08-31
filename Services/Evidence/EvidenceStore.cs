using System.IO;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;

namespace Timenotifier.Services.Evidence;

public interface IEvidenceStore
{
    Task<IReadOnlyList<EvidenceItem>> ReadEvidenceAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task UpsertEvidenceAsync(IEnumerable<EvidenceItem> evidence, CancellationToken cancellationToken = default);
    Task SaveReportDraftAsync(ReportDraft draft, CancellationToken cancellationToken = default);
    Task<ReportDraft?> LoadReportDraftAsync(string reportType, DateOnly startDate, CancellationToken cancellationToken = default);
    Task<ReportDraft?> LoadReportDraftByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportDraft>> ListReportDraftsAsync(
        string? reportType = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteEvidenceAsync(string id, CancellationToken cancellationToken = default);
    Task<int> DeleteEvidenceAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
    Task<bool> DeleteReportDraftAsync(string id, CancellationToken cancellationToken = default);
    Task<int> DeleteReportDraftsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
    Task PruneAutoScreenshotEvidenceAsync(int maxCount, CancellationToken cancellationToken = default);
}

public sealed class EvidenceStore(AppDataPaths paths) : IEvidenceStore
{
    private const int ReportDraftRetentionDays = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new();

    public async Task<IReadOnlyList<EvidenceItem>> ReadEvidenceAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var deletedIds = await ReadDeletedEvidenceIdsAsync(cancellationToken);
        List<EvidenceItem> items = [];
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var path = paths.EvidencePath(date);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                EvidenceItem? item;
                try
                {
                    item = JsonSerializer.Deserialize<EvidenceItem>(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (item is not null && !string.IsNullOrWhiteSpace(item.Id) && !deletedIds.Contains(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        return items
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.CapturedAt).Last())
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }

    public async Task UpsertEvidenceAsync(IEnumerable<EvidenceItem> evidence, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var deletedIds = await ReadDeletedEvidenceIdsAsync(cancellationToken);
        var grouped = evidence
            .Where(item => !deletedIds.Contains(item.Id))
            .GroupBy(item => DateOnly.FromDateTime(item.CapturedAt));
        foreach (var group in grouped)
        {
            var path = paths.EvidencePath(group.Key);
            var existing = (await ReadEvidenceAsync(group.Key, group.Key, cancellationToken))
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var item in group)
            {
                existing[item.Id] = item;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = existing.Values
                .OrderBy(item => item.CapturedAt)
                .Select(item => JsonSerializer.Serialize(item, JsonLineOptions));
            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }
    }

    public async Task SaveReportDraftAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var now = DateTime.Now;
        await PruneExpiredReportDraftsAsync(now, cancellationToken);
        draft.UpdatedAt = now;
        if (draft.Status == ReportStatuses.Confirmed && draft.ConfirmedAt is null)
        {
            draft.ConfirmedAt = draft.UpdatedAt;
        }

        var path = ReportDraftPath(draft.Type, draft.StartDate, draft.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, draft, JsonOptions, cancellationToken);
    }

    public async Task<ReportDraft?> LoadReportDraftAsync(
        string reportType,
        DateOnly startDate,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await PruneExpiredReportDraftsAsync(DateTime.Now, cancellationToken);
        var path = paths.ReportPath(reportType, startDate);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ReportDraft>(stream, JsonOptions, cancellationToken);
    }

    public async Task<ReportDraft?> LoadReportDraftByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var drafts = await ListReportDraftsAsync(cancellationToken: cancellationToken);
        return drafts.FirstOrDefault(draft => string.Equals(draft.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ReportDraft>> ListReportDraftsAsync(
        string? reportType = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await PruneExpiredReportDraftsAsync(DateTime.Now, cancellationToken);
        var reportsRoot = paths.ReportsDirectory;
        if (!Directory.Exists(reportsRoot))
        {
            return [];
        }

        Dictionary<string, ReportDraft> drafts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(reportsRoot, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await ReadDraftFileAsync(path, cancellationToken);
            if (draft is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(reportType)
                && !string.Equals(draft.Type, reportType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (startDate is not null && draft.EndDate < startDate)
            {
                continue;
            }

            if (endDate is not null && draft.StartDate > endDate)
            {
                continue;
            }

            drafts[draft.Id] = draft;
        }

        return drafts.Values
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenByDescending(draft => draft.CreatedAt)
            .ToList();
    }

    public async Task<bool> DeleteEvidenceAsync(string id, CancellationToken cancellationToken = default)
    {
        return await DeleteEvidenceAsync([id], cancellationToken) > 0;
    }

    public async Task<int> DeleteEvidenceAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var removeIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removeIds.Count == 0 || !Directory.Exists(paths.EvidenceDirectory))
        {
            return 0;
        }

        var deletedCount = 0;
        var deletedIds = await ReadDeletedEvidenceIdsAsync(cancellationToken);
        foreach (var file in Directory.EnumerateFiles(paths.EvidenceDirectory, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.Parse(Path.GetFileNameWithoutExtension(file));
            var items = (await ReadEvidenceAsync(date, date, cancellationToken)).ToList();
            var removed = items.Where(item => removeIds.Contains(item.Id)).ToList();
            if (removed.Count == 0)
            {
                continue;
            }

            foreach (var item in removed)
            {
                TryDeleteFile(item.ImagePath);
                deletedIds.Add(item.Id);
            }

            var kept = items
                .Where(item => !removeIds.Contains(item.Id))
                .OrderBy(item => item.CapturedAt)
                .Select(item => JsonSerializer.Serialize(item, JsonLineOptions));
            await File.WriteAllLinesAsync(file, kept, cancellationToken);
            deletedCount += removed.Count;
        }

        if (deletedCount > 0)
        {
            await SaveDeletedEvidenceIdsAsync(deletedIds, cancellationToken);
        }

        return deletedCount;
    }

    public async Task<bool> DeleteReportDraftAsync(string id, CancellationToken cancellationToken = default)
    {
        return await DeleteReportDraftsAsync([id], cancellationToken) > 0;
    }

    public async Task<int> DeleteReportDraftsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var removeIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removeIds.Count == 0 || !Directory.Exists(paths.ReportsDirectory))
        {
            return 0;
        }

        var deletedCount = 0;
        foreach (var file in Directory.EnumerateFiles(paths.ReportsDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await ReadDraftFileAsync(file, cancellationToken);
            if (draft is null || !removeIds.Contains(draft.Id))
            {
                continue;
            }

            TryDeleteFile(file);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(file));
            deletedCount++;
        }

        return deletedCount;
    }

    public async Task PruneAutoScreenshotEvidenceAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        maxCount = Math.Max(1, maxCount);
        if (!Directory.Exists(paths.EvidenceDirectory))
        {
            return;
        }

        var evidenceFiles = Directory.EnumerateFiles(paths.EvidenceDirectory, "*.jsonl").ToList();
        List<EvidenceItem> allItems = [];
        foreach (var file in evidenceFiles)
        {
            var date = DateOnly.Parse(Path.GetFileNameWithoutExtension(file));
            allItems.AddRange(await ReadEvidenceAsync(date, date, cancellationToken));
        }

        var autoScreenshots = allItems
            .Where(item => item.IsAutoCaptured && item.Kind == EvidenceKinds.Screenshot)
            .OrderBy(item => item.CapturedAt)
            .ToList();
        if (autoScreenshots.Count <= maxCount)
        {
            return;
        }

        var removeIds = autoScreenshots
            .Take(autoScreenshots.Count - maxCount)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in autoScreenshots.Where(item => removeIds.Contains(item.Id)))
        {
            TryDeleteFile(item.ImagePath);
        }

        foreach (var group in allItems.GroupBy(item => DateOnly.FromDateTime(item.CapturedAt)))
        {
            var kept = group
                .Where(item => !removeIds.Contains(item.Id))
                .OrderBy(item => item.CapturedAt)
                .Select(item => JsonSerializer.Serialize(item, JsonLineOptions));
            var path = paths.EvidencePath(group.Key);
            await File.WriteAllLinesAsync(path, kept, cancellationToken);
        }
    }

    private async Task<ReportDraft?> ReadDraftFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var draft = await JsonSerializer.DeserializeAsync<ReportDraft>(stream, JsonOptions, cancellationToken);
            if (draft is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(draft.Id))
            {
                draft.Id = Guid.NewGuid().ToString("N");
            }

            return draft;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task PruneExpiredReportDraftsAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.ReportsDirectory))
        {
            return;
        }

        var cutoff = now.AddDays(-ReportDraftRetentionDays);
        var reportPaths = Directory
            .EnumerateFiles(paths.ReportsDirectory, "*.json", SearchOption.AllDirectories)
            .ToList();
        foreach (var path in reportPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await ReadDraftFileAsync(path, cancellationToken);
            if (draft is null || !IsExpiredReportDraft(draft, cutoff))
            {
                continue;
            }

            TryDeleteFile(path);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(path));
        }
    }

    private static bool IsExpiredReportDraft(ReportDraft draft, DateTime cutoff)
    {
        if (draft.Type is not (ReportTypes.Daily or ReportTypes.Weekly)
            || draft.Status != ReportStatuses.Draft)
        {
            return false;
        }

        var lastUpdatedAt = draft.UpdatedAt == DateTime.MinValue
            ? draft.CreatedAt
            : draft.UpdatedAt;
        return lastUpdatedAt != DateTime.MinValue && lastUpdatedAt < cutoff;
    }

    private async Task<HashSet<string>> ReadDeletedEvidenceIdsAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.DeletedEvidenceIdsPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(paths.DeletedEvidenceIdsPath);
            var ids = await JsonSerializer.DeserializeAsync<List<string>>(stream, JsonOptions, cancellationToken)
                ?? [];
            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveDeletedEvidenceIdsAsync(HashSet<string> ids, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DeletedEvidenceIdsPath)!);
        await using var stream = File.Create(paths.DeletedEvidenceIdsPath);
        await JsonSerializer.SerializeAsync(
            stream,
            ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions,
            cancellationToken);
    }

    private string ReportDraftPath(string reportType, DateOnly startDate, string id)
    {
        var typeDirectory = reportType == ReportTypes.Weekly ? "weekly" : "daily";
        return Path.Combine(paths.ReportsDirectory, typeDirectory, $"{startDate:yyyy-MM-dd}", $"{id}.json");
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
