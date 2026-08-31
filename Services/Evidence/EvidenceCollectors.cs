using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timenotifier.Models;
using Timenotifier.Services.Reporting;

namespace Timenotifier.Services.Evidence;

public readonly record struct EvidenceRange(DateOnly StartDate, DateOnly EndDate)
{
    public bool Contains(DateTime value)
    {
        var date = DateOnly.FromDateTime(value);
        return date >= StartDate && date <= EndDate;
    }
}

public interface IEvidenceCollector
{
    string Name { get; }
    Task<IReadOnlyList<EvidenceItem>> CollectAsync(EvidenceRange range, CancellationToken cancellationToken = default);
}

public sealed class CompositeEvidenceCollector(IEnumerable<IEvidenceCollector> collectors, IEvidenceStore store)
{
    public async Task<IReadOnlyList<EvidenceItem>> CollectAndStoreAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        List<EvidenceItem> collected = [];
        foreach (var collector in collectors)
        {
            collected.AddRange(await collector.CollectAsync(range, cancellationToken));
        }

        if (collected.Count > 0)
        {
            var existing = (await store.ReadEvidenceAsync(range.StartDate, range.EndDate, cancellationToken))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in collected)
            {
                if (existing.TryGetValue(item.Id, out var existingItem))
                {
                    item.Included = existingItem.Included;
                }
            }

            await store.UpsertEvidenceAsync(collected, cancellationToken);
        }

        var stored = await store.ReadEvidenceAsync(range.StartDate, range.EndDate, cancellationToken);
        return stored
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }
}

public sealed class StoredEvidenceCollector(IEvidenceStore store) : IEvidenceCollector
{
    public string Name => "Stored";

    public Task<IReadOnlyList<EvidenceItem>> CollectAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        return store.ReadEvidenceAsync(range.StartDate, range.EndDate, cancellationToken);
    }
}

public sealed class CodexSessionEvidenceCollector(CodexSessionEvidenceReader reader) : IEvidenceCollector
{
    public string Name => "AI Sessions";

    public Task<IReadOnlyList<EvidenceItem>> CollectAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        return reader.ReadAsync(range, cancellationToken);
    }
}

public sealed record CodexSessionEvidenceSource(string Name, string Home, string IdPrefix);

public sealed class CodexSessionEvidenceReader
{
    private const int ReadRetryCount = 3;
    private const int MaxSessionGoals = 5;
    private const int MaxSessionResults = 6;
    private const int MaxSessionSummaryLength = 900;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(120);
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string ApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private readonly IReadOnlyList<CodexSessionEvidenceSource> _sources;

    public CodexSessionEvidenceReader(string? codexHome = null)
        : this([new CodexSessionEvidenceSource("Codex", codexHome ?? DefaultHome(".codex"), "codex")])
    {
    }

    public CodexSessionEvidenceReader(IEnumerable<CodexSessionEvidenceSource> sources)
    {
        _sources = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Home))
            .GroupBy(source => Path.GetFullPath(Environment.ExpandEnvironmentVariables(source.Home)), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var source = group.First();
                return source with
                {
                    Home = group.Key,
                    Name = string.IsNullOrWhiteSpace(source.Name) ? Path.GetFileName(group.Key) : source.Name.Trim(),
                    IdPrefix = NormalizeSourceKey(source.IdPrefix)
                };
            })
            .ToList();
    }

    public static IReadOnlyList<CodexSessionEvidenceSource> CreateDefaultSources()
    {
        return
        [
            new("Codex", DefaultHome(".codex"), "codex"),
            new("Trae", DefaultHome(".trae"), "trae"),
            new("Trae CN", DefaultHome(".trae-cn"), "trae-cn"),
            new("Trae CN", DefaultAppDataHome("Trae CN", "ModularData", "ai-agent"), "trae-cn")
        ];
    }

    public async Task<IReadOnlyList<EvidenceItem>> ReadAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        List<EvidenceItem> evidence = [];
        foreach (var source in _sources)
        {
            var index = await ReadIndexAsync(source.Home, cancellationToken);
            var sessionsDirectory = Path.Combine(source.Home, "sessions");
            if (!Directory.Exists(sessionsDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories))
            {
                var items = await TryReadSessionAsync(
                    file,
                    source,
                    index,
                    range,
                    _sources.Count > 1,
                    cancellationToken);
                evidence.AddRange(items);
            }
        }

        return evidence
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }

    private static async Task<Dictionary<string, CodexIndexEntry>> ReadIndexAsync(
        string sourceHome,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(sourceHome, "session_index.jsonl");
        Dictionary<string, CodexIndexEntry> index = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(indexPath))
        {
            return index;
        }

        foreach (var line in await File.ReadAllLinesAsync(indexPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<CodexIndexEntry>(line);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                {
                    index[entry.Id] = entry;
                }
            }
            catch (JsonException)
            {
            }
        }

        return index;
    }

    private static async Task<IReadOnlyList<EvidenceItem>> TryReadSessionAsync(
        string path,
        CodexSessionEvidenceSource source,
        IReadOnlyDictionary<string, CodexIndexEntry> index,
        EvidenceRange range,
        bool includeSourceName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ReadRetryCount; attempt++)
        {
            try
            {
                return await ReadSessionAsync(path, source, index, range, includeSourceName, cancellationToken);
            }
            catch (Exception) when (attempt < ReadRetryCount)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken);
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return [];
    }

    private static async Task<IReadOnlyList<EvidenceItem>> ReadSessionAsync(
        string path,
        CodexSessionEvidenceSource source,
        IReadOnlyDictionary<string, CodexIndexEntry> index,
        EvidenceRange range,
        bool includeSourceName,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        string? cwd = null;
        Dictionary<DateOnly, DailySessionActivity> byDate = [];

        foreach (var line in await ReadLinesAllowingWritersAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = TryParseJsonLine(line);
            if (document is null)
            {
                continue;
            }

            var root = document.RootElement;
            var timestamp = ReadTimestamp(root);
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "session_meta" && root.TryGetProperty("payload", out var meta))
            {
                sessionId = ReadString(meta, "session_id") ?? ReadString(meta, "id") ?? sessionId;
                cwd = ReadString(meta, "cwd") ?? cwd;
                continue;
            }

            if (timestamp is null || !range.Contains(timestamp.Value))
            {
                continue;
            }

            var date = DateOnly.FromDateTime(timestamp.Value);
            var activity = byDate.TryGetValue(date, out var existing)
                ? existing
                : new DailySessionActivity();
            activity.LastAt = timestamp.Value;
            if (root.TryGetProperty("payload", out var payload))
            {
                if (type == "event_msg")
                {
                    var payloadType = ReadString(payload, "type");
                    if (payloadType == "user_message")
                    {
                        AddDistinctSummary(activity.Goals, ReadString(payload, "message"), MaxSessionGoals);
                    }
                    else if (payloadType == "agent_message")
                    {
                        AddDistinctSummary(activity.Results, ReadString(payload, "message"), MaxSessionResults);
                    }
                }
                else if (type == "response_item" && ReadString(payload, "type") == "message")
                {
                    AddDistinctSummary(activity.Results, ReadMessageContent(payload), MaxSessionResults);
                }
            }
            byDate[date] = activity;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        index.TryGetValue(sessionId, out var indexEntry);
        var title = !string.IsNullOrWhiteSpace(indexEntry?.ThreadName)
            ? indexEntry.ThreadName
            : Path.GetFileNameWithoutExtension(path);
        return byDate.Select(pair =>
        {
            var activity = pair.Value;
            var summaryParts = new[]
            {
                includeSourceName ? $"来源：{source.Name}" : null,
                string.IsNullOrWhiteSpace(cwd) ? null : $"工作目录：{cwd}",
                activity.Goals.Count == 0 ? null : $"目标概览：{FormatNumberedSummary(activity.Goals)}",
                activity.Results.Count == 0 ? null : $"工作结果概括：{FormatNumberedSummary(activity.Results)}"
            }.Where(part => !string.IsNullOrWhiteSpace(part));
            return new EvidenceItem
            {
                Id = $"{source.IdPrefix}:{sessionId}:{pair.Key:yyyyMMdd}",
                Kind = EvidenceKindForSource(source),
                CapturedAt = activity.LastAt,
                Title = title,
                Summary = TrimToLength(string.Join(Environment.NewLine, summaryParts), MaxSessionSummaryLength),
                SourcePath = path,
                ThreadId = sessionId,
                Included = true
            };
        }).ToList();
    }

    private sealed class DailySessionActivity
    {
        public DateTime LastAt { get; set; }
        public List<string> Goals { get; } = [];
        public List<string> Results { get; } = [];
    }

    private static string EvidenceKindForSource(CodexSessionEvidenceSource source)
    {
        return string.Equals(source.IdPrefix, "trae-cn", StringComparison.OrdinalIgnoreCase)
            ? EvidenceKinds.TraeCnSession
            : EvidenceKinds.CodexSession;
    }

    private static string DefaultHome(string directoryName)
    {
        return Path.Combine(UserProfile, directoryName);
    }

    private static string DefaultAppDataHome(params string[] segments)
    {
        return Path.Combine([ApplicationData, .. segments]);
    }

    private static string NormalizeSourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "session";
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized) ? "session" : normalized;
    }

    private static async Task<IReadOnlyList<string>> ReadLinesAllowingWritersAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        List<string> lines = [];
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static JsonDocument? TryParseJsonLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? ReadTimestamp(JsonElement root)
    {
        var timestamp = ReadString(root, "timestamp");
        return DateTime.TryParse(timestamp, out var value) ? value.ToLocalTime() : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? ReadMessageContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> texts = [];
        foreach (var item in content.EnumerateArray())
        {
            var text = ReadString(item, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }

        return texts.Count == 0 ? null : string.Join(Environment.NewLine, texts);
    }

    private static string? TrimForSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        normalized = ReportQualityFilter.CleanCodexSummaryCandidate(normalized);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private static void AddDistinctSummary(List<string> summaries, string? value, int maxCount)
    {
        var summary = TrimForSummary(value);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        if (summaries.Any(existing => IsSameOrContained(existing, summary)))
        {
            return;
        }

        summaries.Add(summary);
        if (summaries.Count > maxCount)
        {
            summaries.RemoveAt(0);
        }
    }

    private static bool IsSameOrContained(string existing, string candidate)
    {
        return string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)
            || existing.Contains(candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.Contains(existing, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNumberedSummary(IReadOnlyList<string> summaries)
    {
        return string.Join("；", summaries.Select((summary, index) => $"{index + 1}. {summary}"));
    }

    private static string TrimToLength(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private sealed class CodexIndexEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("thread_name")]
        public string ThreadName { get; set; } = "";
    }
}
