using System.Text;
using System.Text.RegularExpressions;
using Timenotifier.Services.Reporting;

namespace Timenotifier.Views.Assistant;

internal static class AssistantOutputFormatter
{
    private static readonly Regex SummaryFieldPattern = new(
        @"^\s*(?<label>来源|工作目录|目标概览|工作结果概括)\s*[：:]\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeMarkdown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            if (TryAppendLegacyEvidenceFields(line, result))
            {
                continue;
            }

            result.AppendLine(line.TrimEnd());
        }

        return result.ToString().Trim();
    }

    private static bool TryAppendLegacyEvidenceFields(string line, StringBuilder target)
    {
        var fields = line.Contains('｜') || line.Contains('|')
            ? line.Split(['｜', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [line];
        var recognized = false;

        foreach (var field in fields)
        {
            var match = SummaryFieldPattern.Match(field);
            if (!match.Success)
            {
                if (fields.Length > 1)
                {
                    var plainText = EvidenceSummaryDisplayFormatter.ToPlainText(field);
                    if (!string.IsNullOrWhiteSpace(plainText))
                    {
                        target.AppendLine(plainText);
                    }
                }

                continue;
            }

            recognized = true;
            var label = match.Groups["label"].Value;
            var displayValue = match.Groups["value"].Value;
            if (label is "目标概览" or "工作结果概括")
            {
                target.AppendLine($"**{label}**");
                var items = EvidenceSummaryDisplayFormatter.SplitItems(displayValue);
                foreach (var item in items)
                {
                    target.AppendLine($"- {item}");
                }
            }
            else
            {
                target.AppendLine($"**{label}**：{EvidenceSummaryDisplayFormatter.ToPlainText(displayValue)}");
            }
        }

        return recognized;
    }
}
