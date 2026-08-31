using System.Text;
using System.Text.RegularExpressions;
using Timenotifier.Models;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Assistant;

public sealed record AssistantContextSnapshot(
    IReadOnlyList<AssistantModule> Modules,
    string Text);

public interface IAssistantContextProvider
{
    Task<AssistantContextSnapshot> BuildAsync(
        AssistantModule selectedModule,
        string message,
        CancellationToken cancellationToken = default);
}

public sealed partial class AssistantContextProvider(
    IAppSettingsStore settingsStore,
    IEvidenceStore evidenceStore,
    ITimeProvider timeProvider) : IAssistantContextProvider
{
    private const int MaxEvidenceItems = 50;

    public async Task<AssistantContextSnapshot> BuildAsync(
        AssistantModule selectedModule,
        string message,
        CancellationToken cancellationToken = default)
    {
        var modules = SelectModules(selectedModule, message);
        if (modules.Count == 0)
        {
            return new AssistantContextSnapshot([], "本次未装载具体模块数据。");
        }

        var text = new StringBuilder();
        var evidenceLoaded = false;
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (module)
            {
                case AssistantModule.Reminders:
                    await AppendRemindersAsync(text, cancellationToken);
                    break;
                case AssistantModule.Model:
                    await AppendModelAsync(text, cancellationToken);
                    break;
                case AssistantModule.Evidence:
                case AssistantModule.Review:
                    if (!evidenceLoaded)
                    {
                        await AppendEvidenceAsync(text, message, cancellationToken);
                        evidenceLoaded = true;
                    }
                    break;
                case AssistantModule.Reports:
                    await AppendReportsAsync(text, cancellationToken);
                    if (!evidenceLoaded)
                    {
                        await AppendEvidenceAsync(text, message, cancellationToken);
                        evidenceLoaded = true;
                    }
                    break;
                case AssistantModule.Capture:
                    await AppendCaptureAsync(text, cancellationToken);
                    break;
                case AssistantModule.Tasks:
                    await AppendAutomationAsync(text, cancellationToken);
                    break;
            }
        }

        return new AssistantContextSnapshot(modules.AsReadOnly(), text.ToString().Trim());
    }

    private async Task AppendRemindersAsync(StringBuilder text, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        text.AppendLine("[提醒状态]");
        text.AppendLine($"稍后提醒分钟数：{settings.SnoozeMinutes}");
        text.AppendLine($"规则数：{settings.Rules.Count}|日期调整数：{settings.HolidayAdjustments.Count}");
        foreach (var rule in settings.Rules
                     .OrderBy(item => item.Time)
                     .ThenBy(item => item.Id, StringComparer.Ordinal)
                     .Take(50))
        {
            text.AppendLine($"{SafeLabel(rule.Id, 80)}|{rule.Time:HH\\:mm}|{rule.ScheduleKind}|{rule.IsEnabled}|{SafeLabel(rule.Message, 200)}");
        }
        foreach (var item in settings.HolidayAdjustments
                     .OrderBy(item => item.Date)
                     .ThenBy(item => item.Name, StringComparer.Ordinal)
                     .Take(20))
        {
            text.AppendLine($"日期规则|{item.Date:yyyy-MM-dd}|{item.Kind}|{SafeLabel(item.Name, 80)}");
        }
    }

    private async Task AppendModelAsync(StringBuilder text, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        text.AppendLine("[模型状态]");
        text.AppendLine($"Analysis 已配置：{settings.Analysis.IsConfigured}");
        text.AppendLine($"ImageGeneration 已配置：{settings.ImageGeneration.IsConfigured}");
        text.AppendLine($"质量模式：{settings.QualityMode}");
        text.AppendLine($"视觉分析：{settings.UseVision}");
    }

    private async Task AppendEvidenceAsync(
        StringBuilder text,
        string message,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.Now);
        var (start, end) = SelectEvidenceRange(today, message);
        var evidence = (await evidenceStore.ReadEvidenceAsync(start, end, cancellationToken))
            .OrderByDescending(item => item.CapturedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(MaxEvidenceItems)
            .ToList();

        text.AppendLine($"[证据 {start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}，最多 {MaxEvidenceItems} 条]");
        foreach (var item in evidence)
        {
            var tags = string.Join('、', item.Tags
                .Select(tag => SafeLabel(tag, 60))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Take(20));
            text.AppendLine(
                $"{SafeLabel(item.Id, 100)}|{item.CapturedAt:yyyy-MM-dd HH:mm}|{SafeLabel(item.DisplayKind, 40)}|" +
                $"{SafeLabel(item.Title, 160)}|{SafeLabel(item.Summary, 500)}|标签:{tags}|纳入:{item.Included}");
        }
    }

    private async Task AppendReportsAsync(StringBuilder text, CancellationToken cancellationToken)
    {
        var drafts = (await evidenceStore.ListReportDraftsAsync(cancellationToken: cancellationToken))
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenBy(draft => draft.Id, StringComparer.Ordinal)
            .Take(20)
            .ToList();
        var templates = await settingsStore.LoadReportTemplatesAsync(cancellationToken);
        text.AppendLine("[报告状态]");
        text.AppendLine($"历史草稿数（最多展示 20）：{drafts.Count}");
        foreach (var draft in drafts)
        {
            text.AppendLine($"{SafeLabel(draft.Id, 100)}|{draft.DisplayType}|{draft.DisplayRange}|{draft.DisplayStatus}|{draft.DisplayUpdatedAt}");
        }

        text.AppendLine($"[报告模板，仅安全名称，共 {templates.Count} 个]");
        foreach (var template in templates
                     .OrderBy(item => item.Name, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal)
                     .Take(50))
        {
            text.AppendLine($"{SafeLabel(template.Id, 100)}|{SafeLabel(template.Name, 120)}|{template.DisplayScope}");
        }
    }

    private async Task AppendCaptureAsync(StringBuilder text, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadEvidenceCaptureSettingsAsync(cancellationToken);
        var tags = string.Join('、', settings.DefaultTags.Select(tag => SafeLabel(tag, 60)).Take(20));
        text.AppendLine("[采集状态]");
        text.AppendLine(
            $"自动截图:{settings.AutoScreenshotEnabled}|间隔:{settings.ScreenshotIntervalMinutes}|" +
            $"最多:{settings.MaxAutoScreenshots}|快捷键:{SafeLabel(settings.ManualNoteHotkey, 40)}|" +
            $"开机启动:{settings.StartWithWindows}|OCR:{settings.OcrEnabled}|默认标签:{tags}");
    }

    private async Task AppendAutomationAsync(StringBuilder text, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        text.AppendLine("[日报任务状态]");
        text.AppendLine(
            $"启用:{settings.IsEnabled}|时间:{settings.RunTime:HH\\:mm}|项目:{SafeLabel(settings.ProjectName, 120)}|" +
            $"任务:{SafeLabel(settings.TaskName, 120)}|类型:{SafeLabel(settings.TaskType, 60)}|工作量:{settings.WorkloadPercent}%");
    }

    private static (DateOnly Start, DateOnly End) SelectEvidenceRange(DateOnly today, string message)
    {
        if (ContainsAny(message, "本周", "周报", "工作周", "workweek", "this week", "weekly"))
        {
            var week = InternshipWeekCalculator.GetWeek(today);
            return (week.StartDate, week.EndDate);
        }

        return (today, today);
    }

    private static List<AssistantModule> SelectModules(AssistantModule selectedModule, string message)
    {
        HashSet<AssistantModule> inferredModules = [];
        if (ContainsAny(message, "提醒", "闹钟", "稍后", "节假日", "reminder"))
        {
            inferredModules.Add(AssistantModule.Reminders);
        }
        if (ContainsAny(message, "模型", "API", "视觉", "质量模式", "model"))
        {
            inferredModules.Add(AssistantModule.Model);
        }
        if (ContainsAny(message, "证据", "截图", "工作记录", "evidence"))
        {
            inferredModules.Add(AssistantModule.Evidence);
        }
        if (ContainsAny(message, "回顾", "复盘", "review"))
        {
            inferredModules.Add(AssistantModule.Review);
        }
        if (ContainsAny(message, "日报", "周报", "报告", "草稿", "导出", "模板", "report"))
        {
            inferredModules.Add(AssistantModule.Reports);
        }
        if (ContainsAny(message, "采集", "快捷键", "OCR", "自动截图", "capture"))
        {
            inferredModules.Add(AssistantModule.Capture);
        }
        if (ContainsAny(message, "任务", "自动化", "登录", "PM", "提交", "automation"))
        {
            inferredModules.Add(AssistantModule.Tasks);
        }

        var modules = selectedModule == AssistantModule.Assistant
            ? []
            : new List<AssistantModule> { selectedModule };
        modules.AddRange(inferredModules
            .Where(module => module != selectedModule)
            .OrderBy(module => module)
            .Take(3 - modules.Count));
        return modules;
    }

    private static bool ContainsAny(string value, params string[] markers)
    {
        return markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeLabel(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "无";
        }

        var clean = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace('|', '｜')
            .Trim();
        while (clean.Contains("  ", StringComparison.Ordinal))
        {
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (SensitiveDisplayValueRegex().IsMatch(clean))
        {
            return "已隐藏";
        }

        return clean.Length <= maxLength ? clean : clean[..maxLength] + "...";
    }

    [GeneratedRegex(@"(?i)(https?://|file:/|[a-z]:\\|\\\\|\bsk-[a-z0-9_-]{8,}|-----BEGIN [A-Z ]+PRIVATE KEY-----)")]
    private static partial Regex SensitiveDisplayValueRegex();
}
