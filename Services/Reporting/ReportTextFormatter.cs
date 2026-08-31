using System.Text.RegularExpressions;

namespace Timenotifier.Services.Reporting;

public static class ReportTextFormatter
{
    private static readonly Regex HeadingPattern = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex ListPattern = new(@"^\s*([-*+]|\d+\.)\s+", RegexOptions.Compiled);
    private static readonly Regex EmphasisPattern = new(@"[*_`]+", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var lines = markdown
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line =>
            {
                var text = HeadingPattern.Replace(line, "");
                text = ListPattern.Replace(text, "");
                text = LinkPattern.Replace(text, "$1");
                text = EmphasisPattern.Replace(text, "");
                return text.TrimEnd();
            });

        return string.Join(Environment.NewLine, lines).Trim();
    }
}
