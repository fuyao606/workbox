using System.IO;
using Timenotifier.Models;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Automation;

public sealed class DailyReportAutomationService(
    IAppSettingsStore settingsStore,
    IEvidenceStore evidenceStore,
    ReportGenerationService reportGeneration,
    IPlaywrightCliAutomationRunner runner,
    AppDataPaths paths,
    ITimeProvider timeProvider)
{
    public async Task<DailyReportAutomationResult> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.Now);
        var evidence = (await evidenceStore.ReadEvidenceAsync(today, today, cancellationToken))
            .Where(item => item.Included)
            .ToList();
        var result = await reportGeneration.GenerateFromEvidenceAsync(
            ReportTypes.Daily,
            today,
            today,
            evidence,
            null,
            cancellationToken);
        var dailyText = ReportTextFormatter.ToPlainText(result.Draft.DailySummary);
        if (string.IsNullOrWhiteSpace(dailyText))
        {
            dailyText = ReportTextFormatter.ToPlainText(result.Draft.Content);
        }

        var screenshotDirectory = paths.AutomationScreenshotDirectory(today);
        Directory.CreateDirectory(screenshotDirectory);
        var beforePath = Path.Combine(screenshotDirectory, "before-submit.png");
        await runner.FillDailyReportAsync(settings, dailyText, beforePath, cancellationToken);

        return new DailyReportAutomationResult
        {
            Draft = result.Draft,
            DailyText = dailyText,
            BeforeSubmitScreenshotPath = beforePath,
            StatusMessage = "日报已生成并填写到页面，等待确认提交。"
        };
    }

    public async Task SubmitAsync(DailyReportAutomationResult result, string dailyText, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.Now);
        var screenshotDirectory = paths.AutomationScreenshotDirectory(today);
        Directory.CreateDirectory(screenshotDirectory);
        var afterPath = Path.Combine(screenshotDirectory, "after-submit.png");
        await runner.SubmitDailyReportAsync(settings, dailyText, afterPath, cancellationToken);
        result.DailyText = dailyText;
        result.AfterSubmitScreenshotPath = afterPath;
        result.StatusMessage = "已点击提交，请查看提交后的页面截图。";
    }

    public async Task OpenLoginBrowserAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        await runner.OpenLoginBrowserAsync(settings, cancellationToken);
    }
}
