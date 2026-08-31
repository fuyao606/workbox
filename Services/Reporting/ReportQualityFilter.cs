using System.Text.RegularExpressions;

namespace Timenotifier.Services.Reporting;

public static class ReportQualityFilter
{
    private const int MaxDailyItems = 5;

    private static readonly Regex NumberedLinePattern = new(@"^\s*(\d+)[、.．]\s*(.*)$", RegexOptions.Compiled);

    private static readonly string[] NoContentMarkers =
    [
        "暂无可确认内容",
        "暂无可确认问题",
        "暂无明确内容",
        "暂无明确问题"
    ];

    private static readonly string[] LowValueMarkers =
    [
        "工具调用",
        "路径：",
        "工作目录：",
        "退出命令正常",
        "命令正常退出",
        "点击按钮",
        "补齐 /v1 按钮",
        "补齐 v1 按钮",
        "后台事件方法",
        "打包 exe",
        "打包为 exe",
        "发布测试包",
        "生成 zip",
        "测试包",
        "dotnet test"
    ];

    private static readonly string[] VerificationMarkers =
    [
        "测试通过",
        "验证通过",
        "已通过",
        "回归通过"
    ];

    private static readonly string[] WorkValueMarkers =
    [
        "实现",
        "完成",
        "修复",
        "优化",
        "新增",
        "删除",
        "移除",
        "调整",
        "生成",
        "输出",
        "产出",
        "推进",
        "完善",
        "梳理",
        "明确",
        "学习",
        "练习",
        "调试台",
        "导图",
        "Embedding",
        "RAG",
        "LangChain",
        "文档",
        "代码",
        "接口",
        "功能",
        "模型",
        "报告",
        "配置",
        "适配",
        "证据",
        "采集"
    ];

    public static string? CleanCodexSummaryCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = NormalizeSpaces(value);
        var segments = SplitSegments(text)
            .Where(segment => !IsLowValueSegment(segment))
            .ToList();
        if (segments.Count == 0)
        {
            return null;
        }

        var cleaned = string.Join("，", segments);
        return TrimToLength(cleaned, 240);
    }

    public static string CleanDailySummary(string value)
    {
        var candidates = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => StripNumbering(line.Trim()))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsNoContentLine(line))
            .Where(line => !IsLowValueStandaloneLine(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((line, index) => new { Line = line, Index = index, Score = ScoreReportLine(line) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(MaxDailyItems)
            .OrderBy(item => item.Index)
            .Select(item => item.Line)
            .ToList();

        if (candidates.Count == 0)
        {
            return "1、暂无可确认内容";
        }

        return string.Join(Environment.NewLine, candidates.Select((line, index) => $"{index + 1}、{line}"));
    }

    public static bool IsLowValueStandaloneLine(string value)
    {
        var line = NormalizeSpaces(StripNumbering(value));
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        return IsNoContentLine(line)
            || line.Contains("退出命令正常", StringComparison.OrdinalIgnoreCase)
            || line.Contains("命令正常退出", StringComparison.OrdinalIgnoreCase)
            || ((line.Contains("打包", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("发布", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("zip", StringComparison.OrdinalIgnoreCase))
                && (line.Contains("exe", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("测试包", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("zip", StringComparison.OrdinalIgnoreCase)))
            || (ContainsAny(line, LowValueMarkers) && !ContainsAny(line, WorkValueMarkers))
            || (ContainsAny(line, VerificationMarkers) && !HasConcreteVerificationSubject(line));
    }

    private static int ScoreReportLine(string line)
    {
        var score = 0;
        foreach (var marker in WorkValueMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        foreach (var marker in LowValueMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                score -= 3;
            }
        }

        if (ContainsAny(line, VerificationMarkers) && HasConcreteVerificationSubject(line))
        {
            score += 1;
        }

        return score;
    }

    private static bool IsNoContentLine(string line)
    {
        return ContainsAny(line, NoContentMarkers)
            || line.Contains("代码评审、技术风险和下一步", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("待确认", StringComparison.OrdinalIgnoreCase) && line.Length <= 28);
    }

    private static bool IsLowValueSegment(string segment)
    {
        var text = NormalizeSpaces(segment);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return IsNoContentLine(text)
            || ContainsAny(text, LowValueMarkers)
            || text.Contains("执行工具调用", StringComparison.OrdinalIgnoreCase)
            || text.Contains("工作目录", StringComparison.OrdinalIgnoreCase)
            || text.Contains("路径：", StringComparison.OrdinalIgnoreCase)
            || text.Contains("退出命令正常", StringComparison.OrdinalIgnoreCase)
            || text.Contains("命令正常退出", StringComparison.OrdinalIgnoreCase)
            || ((text.Contains("打包", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("发布", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("zip", StringComparison.OrdinalIgnoreCase))
                && (text.Contains("exe", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("测试包", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("zip", StringComparison.OrdinalIgnoreCase)))
            || (ContainsAny(text, VerificationMarkers) && !HasConcreteVerificationSubject(text));
    }

    private static bool HasConcreteVerificationSubject(string line)
    {
        return ContainsAny(line, WorkValueMarkers)
            && !string.Equals(line, "测试通过", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(line, "验证通过", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(line, "已通过", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitSegments(string value)
    {
        return value
            .Split(['。', '；', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(segment => segment.Split(['，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeSpaces)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));
    }

    private static string StripNumbering(string line)
    {
        var match = NumberedLinePattern.Match(line);
        return match.Success
            ? match.Groups[2].Value.Trim()
            : line.TrimStart('-', '*', '+').Trim();
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
    {
        return markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSpaces(string value)
    {
        var text = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ");
        }

        return text;
    }

    private static string TrimToLength(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
