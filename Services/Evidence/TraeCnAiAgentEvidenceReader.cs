using System.IO;
using System.Text.Json;
using SQLitePCL;
using Timenotifier.Models;
using Timenotifier.Services.Reporting;

namespace Timenotifier.Services.Evidence;

public sealed class TraeCnAiAgentEvidenceCollector(TraeCnAiAgentEvidenceReader reader) : IEvidenceCollector
{
    public string Name => "Trae CN AI Agent";

    public Task<IReadOnlyList<EvidenceItem>> CollectAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        return reader.ReadAsync(range, cancellationToken);
    }
}

public sealed class TraeCnAiAgentEvidenceReader
{
    public const string KeyEnvironmentVariable = "TRAE_CN_AI_AGENT_DB_KEY";

    private const int MaxSessionGoals = 5;
    private const int MaxSessionResults = 6;
    private const int MaxSessionSummaryLength = 900;
    private const string SourceName = "Trae CN";
    private const string IdPrefix = "trae-cn";
    private static int ProviderInitialized;

    private readonly string _home;
    private readonly string? _databaseKey;

    public TraeCnAiAgentEvidenceReader(string? home = null, string? databaseKey = null)
    {
        _home = Path.GetFullPath(home ?? DefaultHome());
        _databaseKey = NormalizeRawHexKey(databaseKey ?? Environment.GetEnvironmentVariable(KeyEnvironmentVariable));
    }

    public static string DefaultHome()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Trae CN",
            "ModularData",
            "ai-agent");
    }

    public async Task<IReadOnlyList<EvidenceItem>> ReadAsync(
        EvidenceRange range,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var databasePath = Path.Combine(_home, "database.db");
        if (_databaseKey is null || !File.Exists(databasePath))
        {
            return [];
        }

        try
        {
            EnsureProviderInitialized();
            using var database = OpenDatabase(databasePath, _databaseKey);
            var messages = QueryMessages(database.Handle, range, cancellationToken);
            return BuildEvidence(messages, databasePath, range);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static List<TraeCnMessageRow> QueryMessages(
        sqlite3 database,
        EvidenceRange range,
        CancellationToken cancellationToken)
    {
        var startSeconds = ToUnixSeconds(range.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        var endSeconds = ToUnixSeconds(range.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        const string sql = """
            SELECT
                s.session_id,
                s.session_title,
                s.created_at,
                s.updated_at,
                p.absolute_path,
                m.message_id,
                m.message_type,
                m.message_role,
                m.message_index,
                m.created_at,
                m.updated_at,
                g.content,
                t.content,
                t.summary
            FROM chat_session s
            JOIN chat_message m ON m.session_id = s.session_id
            LEFT JOIN project p ON p.project_id = s.project_id AND COALESCE(p.deleted_at, 0) = 0
            LEFT JOIN chat_message_general g ON g.message_id = m.message_id AND g.deleted_at = 0
            LEFT JOIN chat_message_task t ON t.message_id = m.message_id AND t.deleted_at = 0
            WHERE s.deleted_at = 0
                AND m.deleted_at = 0
                AND ((m.created_at >= ? AND m.created_at < ?) OR (m.updated_at >= ? AND m.updated_at < ?))
            ORDER BY s.created_at, m.message_index, m.created_at, m.id;
            """;

        sqlite3_stmt? statement = null;
        try
        {
            Check(raw.sqlite3_prepare_v2(database, sql, out statement), database);
            BindInt64(statement, 1, startSeconds);
            BindInt64(statement, 2, endSeconds);
            BindInt64(statement, 3, startSeconds);
            BindInt64(statement, 4, endSeconds);

            List<TraeCnMessageRow> rows = [];
            while (raw.sqlite3_step(statement) == raw.SQLITE_ROW)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new TraeCnMessageRow(
                    ReadText(statement, 0) ?? "",
                    ReadText(statement, 1),
                    ReadInt64(statement, 2),
                    ReadInt64(statement, 3),
                    ReadText(statement, 4),
                    ReadText(statement, 5) ?? "",
                    ReadText(statement, 6) ?? "",
                    ReadText(statement, 7) ?? "",
                    ReadInt64(statement, 8),
                    ReadInt64(statement, 9),
                    ReadInt64(statement, 10),
                    ReadText(statement, 11),
                    ReadText(statement, 12),
                    ReadText(statement, 13)));
            }

            return rows;
        }
        finally
        {
            if (statement is not null)
            {
                raw.sqlite3_finalize(statement);
            }
        }
    }

    private static IReadOnlyList<EvidenceItem> BuildEvidence(
        IReadOnlyList<TraeCnMessageRow> rows,
        string databasePath,
        EvidenceRange range)
    {
        List<EvidenceItem> evidence = [];
        foreach (var session in rows
            .Where(row => !string.IsNullOrWhiteSpace(row.SessionId))
            .GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = session
                .OrderBy(row => row.MessageIndex)
                .ThenBy(row => row.MessageCreatedAt)
                .ToList();
            var first = ordered.First();
            foreach (var dailyGroup in ordered
                .GroupBy(row => DateOnly.FromDateTime(MessageActivityAt(row)))
                .Where(group => range.StartDate <= group.Key && group.Key <= range.EndDate))
            {
                var dailyRows = dailyGroup.ToList();
                List<string> userMessages = [];
                List<string> assistantMessages = [];
                foreach (var row in dailyRows)
                {
                    if (string.Equals(row.MessageRole, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var content in ReadUserMessageContent(row.GeneralContent))
                        {
                            AddDistinctSummary(userMessages, content, MaxSessionGoals);
                        }
                    }
                    else if (string.Equals(row.MessageRole, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var content in ReadAssistantMessageContent(row.TaskContent, row.TaskSummary))
                        {
                            AddDistinctSummary(assistantMessages, content, MaxSessionResults);
                        }
                    }
                }

                if (userMessages.Count == 0 && assistantMessages.Count == 0)
                {
                    continue;
                }

                var title = !string.IsNullOrWhiteSpace(first.SessionTitle)
                    ? first.SessionTitle!
                    : userMessages.FirstOrDefault() ?? $"Trae CN 会话 {first.SessionId}";
                var summaryParts = new[]
                {
                    $"来源：{SourceName}",
                    string.IsNullOrWhiteSpace(first.ProjectPath) ? null : $"工作目录：{first.ProjectPath}",
                    userMessages.Count == 0 ? null : $"目标概览：{FormatNumberedSummary(userMessages)}",
                    assistantMessages.Count == 0 ? null : $"工作结果概括：{FormatNumberedSummary(assistantMessages)}"
                }.Where(part => !string.IsNullOrWhiteSpace(part));

                evidence.Add(new EvidenceItem
                {
                    Id = $"{IdPrefix}:{first.SessionId}:{dailyGroup.Key:yyyyMMdd}",
                    Kind = EvidenceKinds.TraeCnSession,
                    CapturedAt = dailyRows.Max(MessageActivityAt),
                    Title = TrimToLength(title, 160),
                    Summary = TrimToLength(string.Join(Environment.NewLine, summaryParts), MaxSessionSummaryLength),
                    SourcePath = databasePath,
                    ThreadId = first.SessionId,
                    Included = true
                });
            }
        }

        return evidence
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.CapturedAt)
            .ToList();
    }

    private static DateTime MessageActivityAt(TraeCnMessageRow row)
    {
        return FromUnixSeconds(Math.Max(row.MessageCreatedAt, row.MessageUpdatedAt) > 0
            ? Math.Max(row.MessageCreatedAt, row.MessageUpdatedAt)
            : row.SessionCreatedAt);
    }

    private static IEnumerable<string> ReadUserMessageContent(string? content)
    {
        using var document = TryParseJson(content);
        if (document is null)
        {
            yield break;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var text = ReadString(item, "text_content") ?? ReadString(item, "text") ?? ReadString(item, "content");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var text = ReadString(document.RootElement, "text_content")
                ?? ReadString(document.RootElement, "text")
                ?? ReadString(document.RootElement, "content");
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> ReadAssistantMessageContent(string? taskContent, string? taskSummary)
    {
        if (!string.IsNullOrWhiteSpace(taskSummary))
        {
            yield return taskSummary;
        }

        using var document = TryParseJson(taskContent);
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("plan_item", out var planItem)
                || planItem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var thought = ReadString(planItem, "thought");
            if (!string.IsNullOrWhiteSpace(thought))
            {
                yield return thought;
            }

            var reasoning = ReadString(planItem, "reasoning_content");
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                yield return reasoning;
            }
        }
    }

    private static JsonDocument? TryParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
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

    private static string? NormalizeRawHexKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = value.Trim();
        if (key.StartsWith("x'", StringComparison.OrdinalIgnoreCase) && key.EndsWith('\''))
        {
            key = key[2..^1];
        }

        return key.Length > 0 && key.All(Uri.IsHexDigit)
            ? key.ToLowerInvariant()
            : null;
    }

    private static void EnsureProviderInitialized()
    {
        if (Interlocked.Exchange(ref ProviderInitialized, 1) == 0)
        {
            try
            {
                raw.SetProvider(new SQLite3Provider_e_sqlcipher());
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static TraeCnDatabase OpenDatabase(string databasePath, string rawHexKey)
    {
        sqlite3? database = null;
        var rc = raw.sqlite3_open_v2(
            databasePath,
            out database,
            raw.SQLITE_OPEN_READONLY | raw.SQLITE_OPEN_URI,
            null);
        Check(rc, database);

        var wrapper = new TraeCnDatabase(database);
        try
        {
            Exec(database, $"PRAGMA key = \"x'{rawHexKey}'\";");
            Scalar(database, "SELECT count(*) FROM sqlite_master;");
            return wrapper;
        }
        catch
        {
            wrapper.Dispose();
            throw;
        }
    }

    private static void Exec(sqlite3 database, string sql)
    {
        Check(raw.sqlite3_exec(database, sql), database);
    }

    private static string? Scalar(sqlite3 database, string sql)
    {
        sqlite3_stmt? statement = null;
        try
        {
            Check(raw.sqlite3_prepare_v2(database, sql, out statement), database);
            return raw.sqlite3_step(statement) == raw.SQLITE_ROW
                ? ReadText(statement, 0)
                : null;
        }
        finally
        {
            if (statement is not null)
            {
                raw.sqlite3_finalize(statement);
            }
        }
    }

    private static void BindInt64(sqlite3_stmt statement, int index, long value)
    {
        Check(raw.sqlite3_bind_int64(statement, index, value), null);
    }

    private static string? ReadText(sqlite3_stmt statement, int index)
    {
        return raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL
            ? null
            : raw.sqlite3_column_text(statement, index).utf8_to_string();
    }

    private static long ReadInt64(sqlite3_stmt statement, int index)
    {
        return raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL
            ? 0
            : raw.sqlite3_column_int64(statement, index);
    }

    private static void Check(int rc, sqlite3? database)
    {
        if (rc == raw.SQLITE_OK || rc == raw.SQLITE_ROW || rc == raw.SQLITE_DONE)
        {
            return;
        }

        var message = database is null
            ? raw.sqlite3_errstr(rc).utf8_to_string()
            : raw.sqlite3_errmsg(database).utf8_to_string();
        throw new InvalidOperationException($"SQLite rc={rc}: {message}");
    }

    private static long ToUnixSeconds(DateTime value)
    {
        return new DateTimeOffset(value).ToUnixTimeSeconds();
    }

    private static DateTime FromUnixSeconds(long value)
    {
        return value <= 0
            ? DateTime.Now
            : DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
    }

    private sealed record TraeCnMessageRow(
        string SessionId,
        string? SessionTitle,
        long SessionCreatedAt,
        long SessionUpdatedAt,
        string? ProjectPath,
        string MessageId,
        string MessageType,
        string MessageRole,
        long MessageIndex,
        long MessageCreatedAt,
        long MessageUpdatedAt,
        string? GeneralContent,
        string? TaskContent,
        string? TaskSummary);

    private sealed class TraeCnDatabase(sqlite3 handle) : IDisposable
    {
        public sqlite3 Handle { get; } = handle;

        public void Dispose()
        {
            raw.sqlite3_close(Handle);
        }
    }
}
