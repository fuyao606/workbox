using System.Text.RegularExpressions;
using Timenotifier.Models;

namespace Timenotifier.Services.Evidence;

public sealed class EvidenceClusterer
{
    private static readonly Regex SplitPattern = new(@"[\s\-_\\/|:：,，.。()[\]【】]+", RegexOptions.Compiled);

    public IReadOnlyList<EvidenceCluster> Cluster(IReadOnlyList<EvidenceItem> evidence)
    {
        var included = evidence.Where(item => item.Included).OrderBy(item => item.CapturedAt).ToList();
        Dictionary<string, List<EvidenceItem>> groups = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in included)
        {
            var keyword = ExtractKeyword(item);
            if (!groups.TryGetValue(keyword, out var items))
            {
                items = [];
                groups[keyword] = items;
            }

            items.Add(item);
        }

        return groups
            .OrderByDescending(group => group.Value.Count)
            .ThenBy(group => group.Key)
            .Select(group => new EvidenceCluster
            {
                Id = StableClusterId(group.Key),
                Name = group.Key,
                Keywords = [group.Key],
                EvidenceIds = group.Value.Select(item => item.Id).ToList()
            })
            .ToList();
    }

    private static string ExtractKeyword(EvidenceItem item)
    {
        var text = FirstNonEmpty(item.Title, item.Summary, item.DisplayKind);
        var parts = SplitPattern
            .Split(text)
            .Select(part => part.Trim())
            .Where(part => part.Length >= 2)
            .Take(3)
            .ToList();

        if (parts.Count == 0)
        {
            return item.DisplayKind;
        }

        var keyword = string.Join(" ", parts).Trim();
        return keyword.Length <= 24 ? keyword : keyword[..24];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "未分类";
    }

    private static string StableClusterId(string value)
    {
        var hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(value));
        return $"cluster-{hash:x}";
    }
}
