namespace Timenotifier.Models;

public sealed class ReportDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = ReportTypes.Daily;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Content { get; set; } = "";
    public List<string> EvidenceIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = ReportStatuses.Draft;
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public DateTime? ConfirmedAt { get; set; }
    public List<string> EvidenceClusterIds { get; set; } = [];
    public List<string> SourceDailyDraftIds { get; set; } = [];
    public string DailySummary { get; set; } = "";
    public string WorkSummary { get; set; } = "";
    public string Problems { get; set; } = "";
    public string NextWeekPlan { get; set; } = "";
    public string SupportAndSuggestions { get; set; } = "";

    public string DisplayRange => StartDate == EndDate
        ? StartDate.ToString("yyyy-MM-dd")
        : $"{StartDate:yyyy-MM-dd} - {EndDate:yyyy-MM-dd}";

    public string DisplayType => Type == ReportTypes.Weekly ? "周报" : "日报";
    public string DisplayStatus => Status == ReportStatuses.Confirmed ? "已确认" : "草稿";
    public string DisplayUpdatedAt => UpdatedAt.ToString("MM-dd HH:mm");
}

public static class ReportTypes
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
}

public static class ReportStatuses
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";
}

public sealed class ReportTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ReportTypeScope { get; set; } = ReportTemplateScopes.Both;
    public string PromptInstructions { get; set; } = "";
    public bool IsBuiltIn { get; set; }

    public string DisplayScope => ReportTypeScope switch
    {
        ReportTypes.Daily => "日报",
        ReportTypes.Weekly => "周报",
        _ => "通用"
    };
}

public static class ReportTemplateScopes
{
    public const string Daily = ReportTypes.Daily;
    public const string Weekly = ReportTypes.Weekly;
    public const string Both = "Both";
}

public sealed class EvidenceCluster
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> EvidenceIds { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
}
