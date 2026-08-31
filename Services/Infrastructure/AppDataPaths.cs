using System.IO;

namespace Timenotifier.Services.Infrastructure;

public sealed class AppDataPaths
{
    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Timenotifier");
    }

    public string RootDirectory { get; }
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public string EvidenceDirectory => Path.Combine(RootDirectory, "evidence");
    public string DeletedEvidenceIdsPath => Path.Combine(EvidenceDirectory, "deleted-evidence-ids.json");
    public string ScreenshotRootDirectory => Path.Combine(RootDirectory, "screenshots");
    public string ReportsDirectory => Path.Combine(RootDirectory, "reports");
    public string AssistantSessionsDirectory => Path.Combine(RootDirectory, "assistant-sessions");
    public string AutomationScreenshotsDirectory => Path.Combine(RootDirectory, "automation-screenshots");
    public string AutomationBrowserProfileDirectory => Path.Combine(RootDirectory, "browser-profile", "daily-report");

    public string EvidencePath(DateOnly date) => Path.Combine(EvidenceDirectory, $"{date:yyyy-MM-dd}.jsonl");

    public string ScreenshotDirectory(DateOnly date) => Path.Combine(ScreenshotRootDirectory, $"{date:yyyy-MM-dd}");

    public string AutomationScreenshotDirectory(DateOnly date) => Path.Combine(AutomationScreenshotsDirectory, $"{date:yyyy-MM-dd}");

    public string ReportPath(string reportType, DateOnly startDate)
    {
        if (reportType == Timenotifier.Models.ReportTypes.Weekly)
        {
            var week = System.Globalization.ISOWeek.GetWeekOfYear(startDate.ToDateTime(TimeOnly.MinValue));
            return Path.Combine(ReportsDirectory, $"weekly-{startDate:yyyy}-W{week:00}.json");
        }

        return Path.Combine(ReportsDirectory, $"daily-{startDate:yyyy-MM-dd}.json");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(EvidenceDirectory);
        Directory.CreateDirectory(ScreenshotRootDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(AssistantSessionsDirectory);
        Directory.CreateDirectory(AutomationScreenshotsDirectory);
        Directory.CreateDirectory(AutomationBrowserProfileDirectory);
    }
}
