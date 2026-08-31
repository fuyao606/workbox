using System.IO;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;
using ZstdSharp;

namespace Timenotifier.Services.Evidence;

public sealed class DeepSeekHarnessEvidenceCollector(DeepSeekHarnessEvidenceReader reader) : IEvidenceCollector
{
    public string Name => "DeepSeek Harness";

    public Task<IReadOnlyList<EvidenceItem>> CollectAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        return reader.ReadAsync(range, cancellationToken);
    }
}

/// <summary>Reads DSH's local append-only session artifacts without modifying them.</summary>
public sealed class DeepSeekHarnessEvidenceReader
{
    private const int MaxGoals = 5;
    private const int MaxResults = 6;
    private const int MaxSummaryLength = 900;
    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
    private readonly string _root;

    public DeepSeekHarnessEvidenceReader(string? root = null)
    {
        _root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root ?? DefaultRoot));
    }

    public async Task<IReadOnlyList<EvidenceItem>> ReadAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        List<EvidenceItem> evidence = [];
        foreach (var path in Directory.EnumerateFiles(_root, "session.jsonl.zstd", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                evidence.AddRange(await ReadSessionAsync(path, range, cancellationToken));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A torn or incompatible session must not hide other evidence sources.
            }
        }

        return evidence
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }

    private static async Task<IReadOnlyList<EvidenceItem>> ReadSessionAsync(
        string path,
        EvidenceRange range,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var decompressor = new DecompressionStream(file);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);

        string? sessionId = null;
        string? cwd = null;
        string? title = null;
        Dictionary<DateOnly, DailySessionActivity> byDate = [];
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = ReadString(root, "type");
            var at = ReadUnixMilliseconds(root, "time");
            if (type == "session")
            {
                sessionId = ReadString(root, "id") ?? sessionId;
                cwd = ReadString(root, "cwd") ?? cwd;
                continue;
            }

            if (string.IsNullOrWhiteSpace(sessionId) || at is null)
            {
                continue;
            }

            if (type == "session/title")
            {
                title = ReadString(root.GetPropertyOrNull("data"), "title") ?? title;
                continue;
            }

            var date = DateOnly.FromDateTime(at.Value);
            if (!range.Contains(at.Value))
            {
                continue;
            }

            var activity = byDate.TryGetValue(date, out var existing)
                ? existing
                : new DailySessionActivity();
            activity.LastAt = at.Value;
            if (type == "user/message")
            {
                var data = root.GetPropertyOrNull("data");
                if (IsHumanUserMessage(data))
                {
                    AddDistinct(activity.Goals, ReadMessageText(data), MaxGoals);
                }
            }
            else if (type == "assistant/message")
            {
                var data = root.GetPropertyOrNull("data");
                var message = data.GetPropertyOrNull("message");
                AddDistinct(activity.Results, ReadTextBlocks(message.GetPropertyOrNull("content")), MaxResults);
            }
            byDate[date] = activity;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        return byDate.Select(pair =>
        {
            var activity = pair.Value;
            var summaryParts = new[]
            {
                "来源：DeepSeek Harness",
                string.IsNullOrWhiteSpace(cwd) ? null : $"工作目录：{cwd}",
                activity.Goals.Count == 0 ? null : $"目标概览：{Format(activity.Goals)}",
                activity.Results.Count == 0 ? null : $"工作结果概括：{Format(activity.Results)}"
            }.Where(value => !string.IsNullOrWhiteSpace(value));
            return new EvidenceItem
            {
                Id = $"dsh:{sessionId}:{pair.Key:yyyyMMdd}",
                Kind = EvidenceKinds.DeepSeekHarnessSession,
                CapturedAt = activity.LastAt,
                Title = string.IsNullOrWhiteSpace(title) ? sessionId : title,
                Summary = Trim(string.Join(Environment.NewLine, summaryParts), MaxSummaryLength),
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

    private static DateTime? ReadUnixMilliseconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
    }

    private static bool IsHumanUserMessage(JsonElement data)
    {
        var source = data.GetPropertyOrNull("source");
        if (source.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var kind = ReadString(source, "kind");
        return string.IsNullOrWhiteSpace(kind) || string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadMessageText(JsonElement element)
    {
        return ReadTextBlocks(element.GetPropertyOrNull("content"));
    }

    private static string? ReadTextBlocks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return string.Join(Environment.NewLine, element.EnumerateArray()
            .Where(item => string.Equals(ReadString(item, "type"), "text", StringComparison.OrdinalIgnoreCase))
            .Select(item => ReadString(item, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text))!);
    }

    private static void AddDistinct(List<string> values, string? value, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length > 240)
        {
            normalized = normalized[..240] + "...";
        }
        if (values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)
            || existing.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(existing, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add(normalized);
        if (values.Count > maxCount)
        {
            values.RemoveAt(0);
        }
    }

    private static string Format(IReadOnlyList<string> values)
    {
        return string.Join("；", values.Select((value, index) => $"{index + 1}. {value}"));
    }

    private static string Trim(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrNull(this JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            ? property
            : default;
    }
}
