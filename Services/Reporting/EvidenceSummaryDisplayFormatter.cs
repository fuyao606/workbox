using System.Text;
using System.Text.RegularExpressions;

namespace Timenotifier.Services.Reporting;

public sealed class EvidenceSummaryDisplay
{
    public string Source { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyList<EvidenceSummarySection> Sections { get; init; } = [];
}

public sealed class EvidenceSummarySection
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Items { get; init; }
}

/// <summary>
/// Builds a display-only view of a stored evidence summary. The original text remains
/// untouched so reporting and evidence traceability continue to use the captured data.
/// </summary>
public static class EvidenceSummaryDisplayFormatter
{
    private static readonly Regex SummaryFieldPattern = new(
        @"^\s*(?<label>来源|工作目录|目标概览|工作结果概括)\s*[：:]\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberedItemPattern = new(
        @"(?:^|(?<=[；;]))\s*\d{1,2}\s*[.、．]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex MarkdownHeadingPattern = new(
        @"(?m)^\s{0,3}#{1,6}\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownLinkPattern = new(
        @"\[(?<text>[^\]]+)\]\([^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InlineCodePattern = new(
        @"`(?<text>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmphasisPattern = new(
        @"(?<marker>\*\*|__|\*|_)(?<text>.+?)\k<marker>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string CodexRequestMarker = "My request for Codex:";

    public static EvidenceSummaryDisplay Format(string? summary)
    {
        var source = "";
        var workingDirectory = "";
        var goals = new StringBuilder();
        var results = new StringBuilder();
        var notes = new List<string>();
        StringBuilder? activeSection = null;

        foreach (var rawLine in SplitLines(summary))
        {
            var match = SummaryFieldPattern.Match(rawLine);
            if (match.Success)
            {
                var value = match.Groups["value"].Value;
                switch (match.Groups["label"].Value)
                {
                    case "来源":
                        source = ToPlainText(value);
                        activeSection = null;
                        break;
                    case "工作目录":
                        workingDirectory = ToPlainText(value);
                        activeSection = null;
                        break;
                    case "目标概览":
                        AppendSectionText(goals, value);
                        activeSection = goals;
                        break;
                    case "工作结果概括":
                        AppendSectionText(results, value);
                        activeSection = results;
                        break;
                }

                continue;
            }

            if (activeSection is not null)
            {
                AppendSectionText(activeSection, rawLine);
            }
            else
            {
                var note = ToPlainText(rawLine);
                if (!string.IsNullOrWhiteSpace(note))
                {
                    notes.Add(note);
                }
            }
        }

        List<EvidenceSummarySection> sections = [];
        AddSection(sections, "目标概览", SplitItems(goals.ToString()));
        AddSection(sections, "工作结果", SplitItems(results.ToString()));
        AddSection(sections, "摘要", notes.Select(ToPlainText));

        return new EvidenceSummaryDisplay
        {
            Source = source,
            WorkingDirectory = workingDirectory,
            Sections = sections
        };
    }

    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = value.Trim();
        var requestIndex = text.IndexOf(CodexRequestMarker, StringComparison.OrdinalIgnoreCase);
        if (requestIndex >= 0)
        {
            text = text[(requestIndex + CodexRequestMarker.Length)..];
        }

        text = MarkdownLinkPattern.Replace(text, "${text}");
        text = InlineCodePattern.Replace(text, "${text}");
        text = EmphasisPattern.Replace(text, "${text}");
        text = MarkdownHeadingPattern.Replace(text, "");
        text = text.Replace("Files mentioned by the user:", "", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("My request for Codex:", "", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static IReadOnlyList<string> SplitItems(string? value)
    {
        var clean = ToPlainText(value);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return [];
        }

        var matches = NumberedItemPattern.Matches(clean);
        if (matches.Count == 0)
        {
            return [clean];
        }

        List<string> items = [];
        var prefix = ToPlainText(clean[..matches[0].Index].Trim(' ', '；', ';'));
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            items.Add(prefix);
        }
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : clean.Length;
            var item = ToPlainText(clean[start..end].Trim(' ', '；', ';'));
            if (!string.IsNullOrWhiteSpace(item))
            {
                items.Add(item);
            }
        }

        return items.Count == 0 ? [clean] : items;
    }

    private static IEnumerable<string> SplitLines(string? value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AppendSectionText(StringBuilder target, string value)
    {
        var clean = ToPlainText(value);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return;
        }

        if (target.Length > 0)
        {
            target.Append('；');
        }

        target.Append(clean);
    }

    private static void AddSection(
        ICollection<EvidenceSummarySection> target,
        string title,
        IEnumerable<string> values)
    {
        var items = values
            .Select(ToPlainText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        target.Add(new EvidenceSummarySection
        {
            Title = title,
            Items = items
        });
    }
}
