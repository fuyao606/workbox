namespace Timenotifier.Models;

public sealed class DailyReportAutomationSettings
{
    public const string DefaultTargetUrl = "https://example.invalid/daily-report";
    public const string DefaultProjectName = "示例项目";
    public const string DefaultTaskName = "示例任务";
    public const string DefaultTaskType = "开发";

    public bool IsEnabled { get; set; }
    public TimeOnly RunTime { get; set; } = new(17, 50);
    public string TargetUrl { get; set; } = DefaultTargetUrl;
    public string BrowserSessionName { get; set; } = "timenotifier-daily-report";
    public string BrowserProfileDirectory { get; set; } = "";
    public string ProjectName { get; set; } = DefaultProjectName;
    public string TaskName { get; set; } = DefaultTaskName;
    public string TaskType { get; set; } = DefaultTaskType;
    public int WorkloadPercent { get; set; } = 100;

    public string DisplayRunTime => RunTime.ToString("HH:mm");
}

public sealed class DailyReportAutomationResult
{
    public required ReportDraft Draft { get; init; }
    public required string DailyText { get; set; }
    public required string BeforeSubmitScreenshotPath { get; init; }
    public string AfterSubmitScreenshotPath { get; set; } = "";
    public string StatusMessage { get; set; } = "";
}
