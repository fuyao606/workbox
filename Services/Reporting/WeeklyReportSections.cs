using System.Text.RegularExpressions;

namespace Timenotifier.Services.Reporting;

public sealed record WeeklyReportSections(
    string WorkSummary,
    string Problems,
    string NextWeekPlan,
    string SupportAndSuggestions)
{
    public static WeeklyReportSections Empty { get; } = new("", "", "", "");

    public string ProblemsAndSupport => string.Join(
        Environment.NewLine,
        new[] { Problems, SupportAndSuggestions }
            .Where(item => !string.IsNullOrWhiteSpace(item)))
        .Trim();

    public string ToMarkdown()
    {
        return $"""
            ## 本周学习/工作总结
            {WorkSummary}

            ## 存在问题
            {Problems}

            ## 下周学习/工作计划
            {NextWeekPlan}

            ## 所需支持/建议
            {SupportAndSuggestions}
            """;
    }
}

public static class WeeklyReportSectionParser
{
    private static readonly Regex NumberedLinePattern = new(@"^\s*(\d+)[、.．]\s*(.*)$", RegexOptions.Compiled);

    public static WeeklyReportSections Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return WeeklyReportSections.Empty;
        }

        var sections = SplitMarkdownSections(ReportTextFormatter.ToPlainText(content));
        var combinedProblemsAndSupport = GetFirst(sections, "工作中遇到的问题、所需要的支持、解决问题措施及建议");
        var problems = GetFirst(sections, "存在问题", "问题与阻塞", "问题与支持", "问题及建议");
        if (string.IsNullOrWhiteSpace(problems))
        {
            problems = combinedProblemsAndSupport;
        }

        return new WeeklyReportSections(
            NormalizeNumberedLines(GetFirst(sections, "本周学习/工作总结", "完成事项", "本周总结", "工作总结")),
            NormalizeNumberedLines(problems),
            NormalizeNumberedLines(GetFirst(sections, "下周学习/工作计划", "下一步计划", "下周计划", "工作计划")),
            NormalizeNumberedLines(GetFirst(sections, "所需支持/建议", "所需要的支持", "解决问题措施及建议", "支持与建议")));
    }

    public static string ParseDailySummary(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var sections = SplitMarkdownSections(ReportTextFormatter.ToPlainText(content));
        var summary = GetFirst(sections, "今日工作内容简述", "完成事项", "本周学习/工作总结", "工作总结");
        return ReportQualityFilter.CleanDailySummary(NormalizeNumberedLines(string.IsNullOrWhiteSpace(summary)
            ? ReportTextFormatter.ToPlainText(content)
            : summary));
    }

    public static WeeklyReportSections FromDraft(Timenotifier.Models.ReportDraft draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.WorkSummary)
            || !string.IsNullOrWhiteSpace(draft.Problems)
            || !string.IsNullOrWhiteSpace(draft.NextWeekPlan)
            || !string.IsNullOrWhiteSpace(draft.SupportAndSuggestions))
        {
            return new WeeklyReportSections(
                NormalizeNumberedLines(draft.WorkSummary),
                NormalizeNumberedLines(draft.Problems),
                NormalizeNumberedLines(draft.NextWeekPlan),
                NormalizeNumberedLines(draft.SupportAndSuggestions));
        }

        return Parse(draft.Content);
    }

    public static string BuildDailyMarkdown(string dailySummary)
    {
        return $"""
            ## 今日工作内容简述
            {ReportQualityFilter.CleanDailySummary(NormalizeNumberedLines(dailySummary))}
            """;
    }

    public static string BuildWeeklyMarkdown(
        string workSummary,
        string problems,
        string nextWeekPlan,
        string supportAndSuggestions)
    {
        return new WeeklyReportSections(
            NormalizeNumberedLines(workSummary),
            NormalizeNumberedLines(problems),
            NormalizeNumberedLines(nextWeekPlan),
            NormalizeNumberedLines(supportAndSuggestions)).ToMarkdown();
    }

    private static Dictionary<string, string> SplitMarkdownSections(string content)
    {
        Dictionary<string, List<string>> buckets = new(StringComparer.OrdinalIgnoreCase);
        string current = "";
        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var title = NormalizeTitle(line);
            if (IsKnownTitle(title))
            {
                current = title;
                if (!buckets.ContainsKey(current))
                {
                    buckets[current] = [];
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                buckets[current].Add(line);
            }
        }

        return buckets.ToDictionary(
            pair => pair.Key,
            pair => string.Join(Environment.NewLine, pair.Value).Trim(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetFirst(Dictionary<string, string> sections, params string[] names)
    {
        foreach (var name in names)
        {
            if (sections.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static string NormalizeNumberedLines(string value)
    {
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !IsKnownTitle(NormalizeTitle(line)))
            .Select(line =>
            {
                var match = NumberedLinePattern.Match(line);
                return match.Success ? match.Groups[2].Value.Trim() : line.TrimStart('-', '*', '+').Trim();
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(6)
            .ToList();

        if (lines.Count == 0)
        {
            return "1、暂无可确认内容";
        }

        return string.Join(Environment.NewLine, lines.Select((line, index) => $"{index + 1}、{line}"));
    }

    private static string NormalizeTitle(string line)
    {
        return line
            .Trim()
            .TrimStart('#')
            .Trim()
            .TrimEnd(':', '：')
            .Trim();
    }

    private static bool IsKnownTitle(string title)
    {
        return title is "今日工作内容简述"
            or "本周学习/工作总结"
            or "下周学习/工作计划"
            or "工作中遇到的问题、所需要的支持、解决问题措施及建议"
            or "存在问题"
            or "所需支持/建议"
            or "所需要的支持"
            or "解决问题措施及建议"
            or "支持与建议"
            or "完成事项"
            or "问题与阻塞"
            or "下一步计划"
            or "证据来源"
            or "本周总结"
            or "工作总结"
            or "下周计划"
            or "工作计划"
            or "问题与支持"
            or "问题及建议";
    }
}
