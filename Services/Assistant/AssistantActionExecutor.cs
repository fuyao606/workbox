using System.Globalization;
using System.Text;
using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Assistant;

public sealed class AssistantActionException(string message) : InvalidOperationException(message);

public interface IAssistantActionHandler
{
    IReadOnlyDictionary<string, AssistantActionRisk> Actions { get; }
    AssistantActionRisk? GetRisk(string name);
    AssistantActionProposal CreateProposal(AssistantParsedAction action);
    Task<AssistantActionExecutionResult> ExecuteAsync(
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default);
}

public sealed class AssistantActionExecutor(
    IAppSettingsStore settingsStore,
    IEvidenceStore evidenceStore,
    CompositeEvidenceCollector evidenceCollector,
    ReportGenerationService reportGeneration,
    IModelClient modelClient,
    ITimeProvider timeProvider) : IAssistantActionHandler
{
    private const int MaxBatchTargets = 20;
    private static readonly IReadOnlyDictionary<string, AssistantActionRisk> Risks =
        new Dictionary<string, AssistantActionRisk>(StringComparer.OrdinalIgnoreCase)
        {
            ["navigate_module"] = AssistantActionRisk.Immediate,
            ["get_app_overview"] = AssistantActionRisk.Immediate,
            ["list_reminders"] = AssistantActionRisk.Immediate,
            ["get_model_status"] = AssistantActionRisk.Immediate,
            ["list_evidence"] = AssistantActionRisk.Immediate,
            ["get_review_summary"] = AssistantActionRisk.Immediate,
            ["list_report_drafts"] = AssistantActionRisk.Immediate,
            ["list_report_templates"] = AssistantActionRisk.Immediate,
            ["get_capture_status"] = AssistantActionRisk.Immediate,
            ["get_automation_status"] = AssistantActionRisk.Immediate,
            ["preview_evidence_image"] = AssistantActionRisk.Immediate,
            ["upsert_reminder"] = AssistantActionRisk.Confirmation,
            ["delete_reminders"] = AssistantActionRisk.Confirmation,
            ["set_snooze_minutes"] = AssistantActionRisk.Confirmation,
            ["upsert_holiday_adjustment"] = AssistantActionRisk.Confirmation,
            ["delete_holiday_adjustments"] = AssistantActionRisk.Confirmation,
            ["set_model_preferences"] = AssistantActionRisk.Confirmation,
            ["test_model_connection"] = AssistantActionRisk.Confirmation,
            ["refresh_evidence"] = AssistantActionRisk.Confirmation,
            ["set_evidence_included"] = AssistantActionRisk.Confirmation,
            ["delete_evidence"] = AssistantActionRisk.Confirmation,
            ["generate_report"] = AssistantActionRisk.Confirmation,
            ["load_report_draft"] = AssistantActionRisk.Confirmation,
            ["save_current_report"] = AssistantActionRisk.Confirmation,
            ["confirm_current_report"] = AssistantActionRisk.Confirmation,
            ["delete_report_drafts"] = AssistantActionRisk.Confirmation,
            ["new_draft_from_confirmed"] = AssistantActionRisk.Confirmation,
            ["copy_current_report"] = AssistantActionRisk.Confirmation,
            ["export_weekly_report"] = AssistantActionRisk.Confirmation,
            ["set_capture_preferences"] = AssistantActionRisk.Confirmation,
            ["set_automation_preferences"] = AssistantActionRisk.Confirmation,
            ["open_automation_login"] = AssistantActionRisk.Confirmation,
            ["run_daily_automation"] = AssistantActionRisk.Confirmation
        };

    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey",
        "baseUrl",
        "model",
        "screenshotDirectory",
        "browserProfileDirectory",
        "browserSessionName",
        "targetUrl",
        "privacyFilters",
        "promptInstructions",
        "templateContent",
        "sourcePath",
        "imagePath"
    };

    public IReadOnlyDictionary<string, AssistantActionRisk> Actions => Risks;

    public AssistantActionRisk? GetRisk(string name)
    {
        return Risks.TryGetValue(name, out var risk) ? risk : null;
    }

    public AssistantActionProposal CreateProposal(AssistantParsedAction action)
    {
        if (!Risks.TryGetValue(action.Name, out var risk))
        {
            throw new AssistantActionException("模型请求了未授权的动作。");
        }

        if (action.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new AssistantActionException("动作参数必须是 JSON 对象。");
        }

        ValidateArgumentTree(action.Arguments);
        return new AssistantActionProposal
        {
            Name = action.Name,
            Arguments = action.Arguments.Clone(),
            Risk = risk,
            Summary = BuildSummary(action.Name, action.Arguments)
        };
    }

    public async Task<AssistantActionExecutionResult> ExecuteAsync(
        AssistantActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        if (proposal.IsExpired)
        {
            throw new AssistantActionException("动作提案已过期，请重新发起。");
        }

        if (!Risks.TryGetValue(proposal.Name, out var expectedRisk) || expectedRisk != proposal.Risk)
        {
            throw new AssistantActionException("动作授权信息无效。");
        }

        ValidateArgumentTree(proposal.Arguments);
        return proposal.Name switch
        {
            "navigate_module" => Navigate(proposal.Arguments),
            "get_app_overview" => await GetOverviewAsync(cancellationToken),
            "list_reminders" => await ListRemindersAsync(cancellationToken),
            "get_model_status" => await GetModelStatusAsync(cancellationToken),
            "list_evidence" => await ListEvidenceAsync(proposal.Arguments, cancellationToken),
            "get_review_summary" => await GetReviewSummaryAsync(proposal.Arguments, cancellationToken),
            "list_report_drafts" => await ListReportDraftsAsync(cancellationToken),
            "list_report_templates" => await ListReportTemplatesAsync(cancellationToken),
            "get_capture_status" => await GetCaptureStatusAsync(cancellationToken),
            "get_automation_status" => await GetAutomationStatusAsync(cancellationToken),
            "preview_evidence_image" => Host(
                "准备预览证据图片。",
                new AssistantHostCommand(
                    AssistantHostCommandKind.PreviewEvidenceImage,
                    TargetId: RequiredString(proposal.Arguments, "evidenceId"))),
            "upsert_reminder" => await UpsertReminderAsync(proposal.Arguments, cancellationToken),
            "delete_reminders" => await DeleteRemindersAsync(proposal.Arguments, cancellationToken),
            "set_snooze_minutes" => await SetSnoozeAsync(proposal.Arguments, cancellationToken),
            "upsert_holiday_adjustment" => await UpsertHolidayAsync(proposal.Arguments, cancellationToken),
            "delete_holiday_adjustments" => await DeleteHolidaysAsync(proposal.Arguments, cancellationToken),
            "set_model_preferences" => await SetModelPreferencesAsync(proposal.Arguments, cancellationToken),
            "test_model_connection" => await TestModelConnectionAsync(proposal.Arguments, cancellationToken),
            "refresh_evidence" => await RefreshEvidenceAsync(proposal.Arguments, cancellationToken),
            "set_evidence_included" => await SetEvidenceIncludedAsync(proposal.Arguments, cancellationToken),
            "delete_evidence" => await DeleteEvidenceAsync(proposal.Arguments, cancellationToken),
            "generate_report" => await GenerateReportAsync(proposal.Arguments, cancellationToken),
            "load_report_draft" => Host(
                "准备载入报告草稿。",
                new AssistantHostCommand(
                    AssistantHostCommandKind.LoadReportDraft,
                    TargetId: RequiredString(proposal.Arguments, "draftId"))),
            "save_current_report" => Host(
                "准备保存当前报告草稿。",
                new AssistantHostCommand(AssistantHostCommandKind.SaveCurrentReport)),
            "confirm_current_report" => Host(
                "准备确认当前报告草稿。",
                new AssistantHostCommand(AssistantHostCommandKind.ConfirmCurrentReport)),
            "delete_report_drafts" => await DeleteReportDraftsAsync(proposal.Arguments, cancellationToken),
            "new_draft_from_confirmed" => Host(
                "准备从已确认报告创建新草稿。",
                new AssistantHostCommand(
                    AssistantHostCommandKind.NewDraftFromConfirmed,
                    TargetId: RequiredString(proposal.Arguments, "draftId"))),
            "copy_current_report" => Host(
                "准备复制当前报告。",
                new AssistantHostCommand(
                    AssistantHostCommandKind.CopyCurrentReport,
                    Value: OptionalString(proposal.Arguments, "format") ?? "plain")),
            "export_weekly_report" => Host(
                "准备导出当前周报。",
                new AssistantHostCommand(
                    AssistantHostCommandKind.ExportWeeklyReport,
                    Value: OptionalString(proposal.Arguments, "template") ?? "enterprise")),
            "set_capture_preferences" => await SetCapturePreferencesAsync(proposal.Arguments, cancellationToken),
            "set_automation_preferences" => await SetAutomationPreferencesAsync(proposal.Arguments, cancellationToken),
            "open_automation_login" => Host(
                "准备打开日报系统登录浏览器。",
                new AssistantHostCommand(AssistantHostCommandKind.OpenAutomationLogin)),
            "run_daily_automation" => Host(
                "准备启动日报自动化审核流程。",
                new AssistantHostCommand(AssistantHostCommandKind.RunDailyAutomation)),
            _ => throw new AssistantActionException("模型请求了未授权的动作。")
        };
    }

    private static AssistantActionExecutionResult Navigate(JsonElement arguments)
    {
        var module = ParseModule(RequiredString(arguments, "module"));
        return Host(
            $"已切换到{ModuleDisplayName(module)}。",
            new AssistantHostCommand(AssistantHostCommandKind.NavigateModule, module));
    }

    private async Task<AssistantActionExecutionResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var reminders = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        var model = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        var capture = await settingsStore.LoadEvidenceCaptureSettingsAsync(cancellationToken);
        var automation = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        return Success($"""
            ### 当前状态
            - **提醒规则**：{reminders.Rules.Count} 条
            - **模型服务**：{(model.IsConfigured ? "已配置" : "未配置")}
            - **自动截图**：{OnOff(capture.AutoScreenshotEnabled)}
            - **日报任务**：{OnOff(automation.IsEnabled)}
            """);
    }

    private async Task<AssistantActionExecutionResult> ListRemindersAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        var output = new StringBuilder();
        output.AppendLine("### 提醒");
        output.AppendLine($"- **稍后提醒**：{settings.SnoozeMinutes} 分钟");
        if (settings.Rules.Count == 0)
        {
            output.AppendLine("- 暂无提醒规则");
        }
        else
        {
            foreach (var rule in settings.Rules.OrderBy(rule => rule.Time))
            {
                output.AppendLine(
                    $"- **{rule.Time:HH\\:mm}** {MarkdownText(rule.Message)}（{ReminderScheduleDisplay(rule.ScheduleKind)}，{OnOff(rule.IsEnabled)}）");
            }
        }

        return Success(output.ToString().Trim());
    }

    private async Task<AssistantActionExecutionResult> GetModelStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        return Success($"""
            ### 模型状态
            - **文字分析**：{(settings.Analysis.IsConfigured ? "已配置" : "未配置")}
            - **图像生成**：{(settings.ImageGeneration.IsConfigured ? "已配置" : "未配置")}
            - **质量模式**：{settings.QualityMode}
            - **视觉分析**：{OnOff(settings.UseVision)}
            """);
    }

    private async Task<AssistantActionExecutionResult> ListEvidenceAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var (start, end) = ResolveRange(arguments);
        var evidence = (await evidenceStore.ReadEvidenceAsync(start, end, cancellationToken))
            .OrderByDescending(item => item.CapturedAt)
            .Take(50)
            .ToList();
        var output = new StringBuilder();
        output.AppendLine("### 证据列表");
        output.AppendLine($"- **时间范围**：{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}");
        output.AppendLine($"- **证据数量**：{evidence.Count} 条");
        if (evidence.Count == 0)
        {
            output.AppendLine();
            output.AppendLine("暂无证据。");
        }
        else
        {
            foreach (var item in evidence)
            {
                AppendEvidenceMarkdown(output, item);
            }
        }

        return Success(output.ToString().Trim());
    }

    private async Task<AssistantActionExecutionResult> GetReviewSummaryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var (start, end) = ResolveRange(arguments);
        var evidence = await evidenceStore.ReadEvidenceAsync(start, end, cancellationToken);
        return Success($"""
            ### 回顾摘要
            - **时间范围**：{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}
            - **证据总数**：{evidence.Count} 条
            - **已纳入报告**：{evidence.Count(item => item.Included)} 条
            - **截图**：{evidence.Count(item => item.Kind == EvidenceKinds.Screenshot)} 条
            - **手动记录**：{evidence.Count(item => item.Kind == EvidenceKinds.ManualNote)} 条
            """);
    }

    private async Task<AssistantActionExecutionResult> ListReportDraftsAsync(CancellationToken cancellationToken)
    {
        var drafts = (await evidenceStore.ListReportDraftsAsync(cancellationToken: cancellationToken))
            .Take(20)
            .ToList();
        var output = new StringBuilder("### 报告草稿");
        if (drafts.Count == 0)
        {
            output.AppendLine();
            output.AppendLine("暂无报告草稿。");
        }
        else
        {
            foreach (var draft in drafts)
            {
                output.AppendLine();
                output.AppendLine(
                    $"- **{MarkdownText(draft.DisplayType)}** · {MarkdownText(draft.DisplayRange)} · {MarkdownText(draft.DisplayStatus)} · 更新于 {MarkdownText(draft.DisplayUpdatedAt)}");
            }
        }

        return Success(output.ToString().Trim());
    }

    private async Task<AssistantActionExecutionResult> ListReportTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = await settingsStore.LoadReportTemplatesAsync(cancellationToken);
        var output = new StringBuilder("### 报告模板");
        if (templates.Count == 0)
        {
            output.AppendLine();
            output.AppendLine("暂无可用模板。");
        }
        else
        {
            foreach (var template in templates.OrderBy(template => template.Name, StringComparer.Ordinal))
            {
                output.AppendLine();
                output.AppendLine($"- **{MarkdownText(template.Name)}**（{MarkdownText(template.DisplayScope)}）");
            }
        }

        return Success(output.ToString().Trim());
    }

    private async Task<AssistantActionExecutionResult> GetCaptureStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadEvidenceCaptureSettingsAsync(cancellationToken);
        return Success($"""
            ### 采集状态
            - **自动截图**：{OnOff(settings.AutoScreenshotEnabled)}
            - **采集间隔**：{settings.ScreenshotIntervalMinutes} 分钟
            - **最多保留**：{settings.MaxAutoScreenshots} 张
            - **快捷键**：{MarkdownText(settings.ManualNoteHotkey)}
            - **开机启动**：{OnOff(settings.StartWithWindows)}
            - **OCR**：{OnOff(settings.OcrEnabled)}
            - **默认标签**：{MarkdownText(string.Join("、", settings.DefaultTags))}
            """);
    }

    private async Task<AssistantActionExecutionResult> GetAutomationStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        return Success($"""
            ### 日报任务
            - **状态**：{OnOff(settings.IsEnabled)}
            - **执行时间**：{settings.RunTime:HH\\:mm}
            - **项目**：{MarkdownText(settings.ProjectName)}
            - **任务**：{MarkdownText(settings.TaskName)}
            - **类型**：{MarkdownText(settings.TaskType)}
            - **工作量**：{settings.WorkloadPercent}%
            """);
    }

    private async Task<AssistantActionExecutionResult> UpsertReminderAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        var id = OptionalString(arguments, "id");
        var message = RequiredString(arguments, "message").Trim();
        if (message.Length is 0 or > 200)
        {
            throw new AssistantActionException("提醒内容必须为 1-200 个字符。");
        }

        if (!TimeOnly.TryParseExact(RequiredString(arguments, "time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new AssistantActionException("提醒时间必须使用 HH:mm 格式。");
        }

        if (!Enum.TryParse<ReminderScheduleKind>(RequiredString(arguments, "scheduleKind"), true, out var scheduleKind))
        {
            throw new AssistantActionException("提醒周期类型无效。");
        }

        var advanceMinutes = OptionalInt(arguments, "advanceMinutes") ?? 0;
        if (advanceMinutes is < 0 or > 1440)
        {
            throw new AssistantActionException("提前提醒分钟数必须在 0-1440 之间。");
        }

        var weekdays = OptionalStringArray(arguments, "weekdays")
            .Select(ParseDayOfWeek)
            .ToHashSet();
        DateOnly? onceDate = null;
        var onceDateText = OptionalString(arguments, "onceDate");
        if (onceDateText is not null)
        {
            if (!DateOnly.TryParseExact(onceDateText, "yyyy-MM-dd", out var parsedOnceDate))
            {
                throw new AssistantActionException("单次提醒日期必须使用 yyyy-MM-dd 格式。");
            }
            onceDate = parsedOnceDate;
        }
        if (scheduleKind == ReminderScheduleKind.SpecificWeekdays && weekdays.Count == 0)
        {
            throw new AssistantActionException("指定星期提醒至少需要一个星期值。");
        }
        if (scheduleKind == ReminderScheduleKind.Once && onceDate is null)
        {
            throw new AssistantActionException("单次提醒必须提供 onceDate。");
        }

        var existing = id is null ? null : settings.Rules.FirstOrDefault(rule => rule.Id == id);
        var rule = existing ?? new ReminderRule { Id = id ?? Guid.NewGuid().ToString("N") };
        rule.Message = message;
        rule.Time = time;
        rule.ScheduleKind = scheduleKind;
        rule.Weekdays = weekdays;
        rule.OnceDate = onceDate;
        rule.AdvanceMinutes = advanceMinutes;
        rule.IsEnabled = OptionalBool(arguments, "isEnabled") ?? true;
        if (existing is null)
        {
            settings.Rules.Add(rule);
        }

        await settingsStore.SaveReminderSettingsAsync(settings, cancellationToken);
        return Success("提醒规则已保存。", AssistantRefreshTargets.Reminders);
    }

    private async Task<AssistantActionExecutionResult> DeleteRemindersAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ids = RequiredStringArray(arguments, "ids").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        var removed = settings.Rules.RemoveAll(rule => ids.Contains(rule.Id));
        await settingsStore.SaveReminderSettingsAsync(settings, cancellationToken);
        return Success($"已删除 {removed} 条提醒规则。", AssistantRefreshTargets.Reminders);
    }

    private async Task<AssistantActionExecutionResult> SetSnoozeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var minutes = RequiredInt(arguments, "minutes");
        if (minutes is < 1 or > 240)
        {
            throw new AssistantActionException("稍后提醒分钟数必须在 1-240 之间。");
        }
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        settings.SnoozeMinutes = minutes;
        await settingsStore.SaveReminderSettingsAsync(settings, cancellationToken);
        return Success("稍后提醒时长已保存。", AssistantRefreshTargets.Reminders);
    }

    private async Task<AssistantActionExecutionResult> UpsertHolidayAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(RequiredString(arguments, "date"), "yyyy-MM-dd", out var date))
        {
            throw new AssistantActionException("日期规则必须使用 yyyy-MM-dd 格式。");
        }
        if (!Enum.TryParse<HolidayAdjustmentKind>(RequiredString(arguments, "kind"), true, out var kind))
        {
            throw new AssistantActionException("日期规则类型无效。");
        }
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        settings.HolidayAdjustments.RemoveAll(item => item.Date == date);
        settings.HolidayAdjustments.Add(new HolidayAdjustment
        {
            Date = date,
            Kind = kind,
            Name = OptionalString(arguments, "name")?.Trim() ?? ""
        });
        await settingsStore.SaveReminderSettingsAsync(settings, cancellationToken);
        return Success("日期规则已保存。", AssistantRefreshTargets.Reminders);
    }

    private async Task<AssistantActionExecutionResult> DeleteHolidaysAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var dates = RequiredStringArray(arguments, "dates")
            .Select(value => DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)
                ? date
                : throw new AssistantActionException("日期规则必须使用 yyyy-MM-dd 格式。"))
            .ToHashSet();
        var settings = await settingsStore.LoadReminderSettingsAsync(cancellationToken);
        var removed = settings.HolidayAdjustments.RemoveAll(item => dates.Contains(item.Date));
        await settingsStore.SaveReminderSettingsAsync(settings, cancellationToken);
        return Success($"已删除 {removed} 条日期规则。", AssistantRefreshTargets.Reminders);
    }

    private async Task<AssistantActionExecutionResult> SetModelPreferencesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        var qualityText = OptionalString(arguments, "qualityMode");
        if (qualityText is not null)
        {
            if (!Enum.TryParse<ModelQualityMode>(qualityText, true, out var quality))
            {
                throw new AssistantActionException("模型质量模式无效。");
            }
            settings.QualityMode = quality;
        }
        settings.UseVision = OptionalBool(arguments, "useVision") ?? settings.UseVision;
        await settingsStore.SaveModelSettingsAsync(settings, cancellationToken);
        return Success("模型偏好已保存。", AssistantRefreshTargets.Model);
    }

    private async Task<AssistantActionExecutionResult> TestModelConnectionAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var kindText = OptionalString(arguments, "kind") ?? "Analysis";
        if (!Enum.TryParse<ModelConnectionTestKind>(kindText, true, out var kind))
        {
            throw new AssistantActionException("模型连接测试类型无效。");
        }
        var settings = await settingsStore.LoadModelSettingsAsync(cancellationToken);
        var provider = kind == ModelConnectionTestKind.Analysis ? settings.Analysis : settings.ImageGeneration;
        var result = await modelClient.TestConnectionAsync(provider, kind, cancellationToken);
        return new AssistantActionExecutionResult(result.Succeeded, result.Message);
    }

    private async Task<AssistantActionExecutionResult> RefreshEvidenceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var range = ResolveRange(arguments);
        var evidence = await evidenceCollector.CollectAndStoreAsync(new EvidenceRange(range.Start, range.End), cancellationToken);
        return Success($"已刷新 {evidence.Count} 条证据。", AssistantRefreshTargets.Evidence | AssistantRefreshTargets.Review | AssistantRefreshTargets.Reports);
    }

    private async Task<AssistantActionExecutionResult> SetEvidenceIncludedAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ids = RequiredStringArray(arguments, "ids").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var included = RequiredBool(arguments, "included");
        var week = InternshipWeekCalculator.GetWeek(DateOnly.FromDateTime(timeProvider.Now));
        var evidence = (await evidenceStore.ReadEvidenceAsync(week.StartDate, week.EndDate, cancellationToken)).ToList();
        var matched = evidence.Where(item => ids.Contains(item.Id)).ToList();
        if (matched.Count != ids.Count)
        {
            throw new AssistantActionException("部分证据已不存在或不在当前工作周，请刷新后重试。");
        }
        foreach (var item in matched)
        {
            item.Included = included;
        }
        await evidenceStore.UpsertEvidenceAsync(matched, cancellationToken);
        return Success($"已更新 {matched.Count} 条证据的纳入状态。", AssistantRefreshTargets.Evidence | AssistantRefreshTargets.Review | AssistantRefreshTargets.Reports);
    }

    private async Task<AssistantActionExecutionResult> DeleteEvidenceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ids = RequiredStringArray(arguments, "ids");
        var deleted = await evidenceStore.DeleteEvidenceAsync(ids, cancellationToken);
        return Success($"已删除 {deleted} 条证据。", AssistantRefreshTargets.Evidence | AssistantRefreshTargets.Review | AssistantRefreshTargets.Reports);
    }

    private async Task<AssistantActionExecutionResult> GenerateReportAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var typeText = OptionalString(arguments, "type") ?? ReportTypes.Daily;
        var type = typeText.Equals("weekly", StringComparison.OrdinalIgnoreCase)
            ? ReportTypes.Weekly
            : typeText.Equals("daily", StringComparison.OrdinalIgnoreCase)
                ? ReportTypes.Daily
                : throw new AssistantActionException("报告类型必须是 Daily 或 Weekly。");
        var today = DateOnly.FromDateTime(timeProvider.Now);
        var (start, end) = type == ReportTypes.Weekly
            ? (InternshipWeekCalculator.GetWeek(today).StartDate, InternshipWeekCalculator.GetWeek(today).EndDate)
            : (today, today);
        var evidence = await evidenceCollector.CollectAndStoreAsync(new EvidenceRange(start, end), cancellationToken);
        ReportTemplate? template = null;
        var templateId = OptionalString(arguments, "templateId");
        if (templateId is not null)
        {
            template = (await settingsStore.LoadReportTemplatesAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase))
                ?? throw new AssistantActionException("报告模板已不存在。");
        }
        var generated = await reportGeneration.GenerateFromEvidenceAsync(type, start, end, evidence, template, cancellationToken);
        return new AssistantActionExecutionResult(
            true,
            $"已生成{generated.Draft.DisplayType}草稿。",
            AssistantRefreshTargets.Evidence | AssistantRefreshTargets.Review | AssistantRefreshTargets.Reports,
            new AssistantHostCommand(AssistantHostCommandKind.LoadReportDraft, TargetId: generated.Draft.Id));
    }

    private async Task<AssistantActionExecutionResult> DeleteReportDraftsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ids = RequiredStringArray(arguments, "ids");
        var deleted = await evidenceStore.DeleteReportDraftsAsync(ids, cancellationToken);
        return Success($"已删除 {deleted} 个报告草稿。", AssistantRefreshTargets.Reports);
    }

    private async Task<AssistantActionExecutionResult> SetCapturePreferencesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadEvidenceCaptureSettingsAsync(cancellationToken);
        settings.AutoScreenshotEnabled = OptionalBool(arguments, "autoScreenshotEnabled") ?? settings.AutoScreenshotEnabled;
        settings.ScreenshotIntervalMinutes = OptionalInt(arguments, "screenshotIntervalMinutes") ?? settings.ScreenshotIntervalMinutes;
        settings.MaxAutoScreenshots = OptionalInt(arguments, "maxAutoScreenshots") ?? settings.MaxAutoScreenshots;
        settings.ManualNoteHotkey = OptionalString(arguments, "manualNoteHotkey")?.Trim() ?? settings.ManualNoteHotkey;
        settings.StartWithWindows = OptionalBool(arguments, "startWithWindows") ?? settings.StartWithWindows;
        settings.OcrEnabled = OptionalBool(arguments, "ocrEnabled") ?? settings.OcrEnabled;
        var tags = OptionalStringArray(arguments, "defaultTags");
        if (tags.Count > 0)
        {
            settings.DefaultTags = tags;
        }
        if (settings.ScreenshotIntervalMinutes is < 1 or > 240)
        {
            throw new AssistantActionException("截图间隔必须在 1-240 分钟之间。");
        }
        if (settings.MaxAutoScreenshots is < 1 or > 1000)
        {
            throw new AssistantActionException("截图保留数量必须在 1-1000 之间。");
        }
        await settingsStore.SaveEvidenceCaptureSettingsAsync(settings, cancellationToken);
        return Success("采集设置已保存。", AssistantRefreshTargets.Capture);
    }

    private async Task<AssistantActionExecutionResult> SetAutomationPreferencesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadDailyReportAutomationSettingsAsync(cancellationToken);
        settings.IsEnabled = OptionalBool(arguments, "isEnabled") ?? settings.IsEnabled;
        var runTime = OptionalString(arguments, "runTime");
        if (runTime is not null)
        {
            if (!TimeOnly.TryParseExact(runTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new AssistantActionException("任务执行时间必须使用 HH:mm 格式。");
            }
            settings.RunTime = parsed;
        }
        settings.ProjectName = OptionalString(arguments, "projectName")?.Trim() ?? settings.ProjectName;
        settings.TaskName = OptionalString(arguments, "taskName")?.Trim() ?? settings.TaskName;
        settings.TaskType = OptionalString(arguments, "taskType")?.Trim() ?? settings.TaskType;
        settings.WorkloadPercent = OptionalInt(arguments, "workloadPercent") ?? settings.WorkloadPercent;
        if (settings.WorkloadPercent is < 1 or > 100)
        {
            throw new AssistantActionException("工作量必须在 1-100 之间。");
        }
        await settingsStore.SaveDailyReportAutomationSettingsAsync(settings, cancellationToken);
        return Success("日报自动化设置已保存。", AssistantRefreshTargets.Automation);
    }

    private (DateOnly Start, DateOnly End) ResolveRange(JsonElement arguments)
    {
        var today = DateOnly.FromDateTime(timeProvider.Now);
        var range = OptionalString(arguments, "range") ?? "today";
        if (range.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return (today, today);
        }
        if (range.Equals("workweek", StringComparison.OrdinalIgnoreCase)
            || range.Equals("week", StringComparison.OrdinalIgnoreCase))
        {
            var week = InternshipWeekCalculator.GetWeek(today);
            return (week.StartDate, week.EndDate);
        }
        throw new AssistantActionException("范围必须是 today 或 workweek。");
    }

    private static void ValidateArgumentTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > MaxBatchTargets)
        {
            throw new AssistantActionException("单次批量动作最多包含 20 个目标。");
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SensitiveProperties.Contains(property.Name))
                {
                    throw new AssistantActionException("动作参数包含不允许进入对话的敏感字段。");
                }
                ValidateArgumentTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateArgumentTree(item);
            }
        }
    }

    private static string BuildSummary(string name, JsonElement arguments)
    {
        return name switch
        {
            "navigate_module" => $"打开{ModuleDisplayName(ParseModule(RequiredString(arguments, "module")))}",
            "delete_reminders" => $"删除 {ArrayCount(arguments, "ids")} 条提醒",
            "delete_holiday_adjustments" => $"删除 {ArrayCount(arguments, "dates")} 条日期规则",
            "delete_evidence" => $"删除 {ArrayCount(arguments, "ids")} 条证据",
            "set_evidence_included" => $"更新 {ArrayCount(arguments, "ids")} 条证据的纳入状态",
            "delete_report_drafts" => $"删除 {ArrayCount(arguments, "ids")} 个报告草稿",
            "upsert_reminder" => "新增或更新 1 条提醒",
            "upsert_holiday_adjustment" => "新增或更新 1 条日期规则",
            "set_snooze_minutes" => "更新稍后提醒时长",
            "set_model_preferences" => "更新模型偏好",
            "test_model_connection" => "测试模型连接",
            "refresh_evidence" => "刷新本地证据",
            "generate_report" => "生成报告草稿",
            "set_capture_preferences" => "更新采集设置",
            "set_automation_preferences" => "更新日报自动化设置",
            "open_automation_login" => "打开日报系统登录浏览器",
            "run_daily_automation" => "启动日报自动化审核流程",
            _ => ActionDisplayName(name)
        };
    }

    private static string ActionDisplayName(string name)
    {
        return name.Replace('_', ' ');
    }

    private static AssistantModule ParseModule(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "assistant" => AssistantModule.Assistant,
            "reminder" or "reminders" => AssistantModule.Reminders,
            "model" => AssistantModule.Model,
            "evidence" => AssistantModule.Evidence,
            "review" => AssistantModule.Review,
            "report" or "reports" => AssistantModule.Reports,
            "capture" => AssistantModule.Capture,
            "task" or "tasks" => AssistantModule.Tasks,
            _ => throw new AssistantActionException("未知的配置模块。")
        };
    }

    private static string ModuleDisplayName(AssistantModule module)
    {
        return module switch
        {
            AssistantModule.Assistant => "AI 助手",
            AssistantModule.Reminders => "提醒页",
            AssistantModule.Model => "模型页",
            AssistantModule.Evidence => "证据页",
            AssistantModule.Review => "回顾页",
            AssistantModule.Reports => "报告页",
            AssistantModule.Capture => "采集页",
            AssistantModule.Tasks => "任务页",
            _ => "配置页"
        };
    }

    private static AssistantActionExecutionResult Success(
        string message,
        AssistantRefreshTargets refreshTargets = AssistantRefreshTargets.None)
    {
        return new AssistantActionExecutionResult(true, message, refreshTargets);
    }

    private static AssistantActionExecutionResult Host(string message, AssistantHostCommand command)
    {
        return new AssistantActionExecutionResult(true, message, HostCommand: command);
    }

    private static void AppendEvidenceMarkdown(StringBuilder target, EvidenceItem item)
    {
        var summary = EvidenceSummaryDisplayFormatter.Format(item.Summary);
        target.AppendLine();
        target.AppendLine($"#### {item.CapturedAt:MM-dd HH:mm} · {MarkdownText(item.DisplayKind)}");
        target.AppendLine($"**{MarkdownText(item.Title)}**");
        target.AppendLine($"- **报告状态**：{(item.Included ? "已纳入" : "未纳入")}");
        if (!string.IsNullOrWhiteSpace(item.DisplayTags))
        {
            target.AppendLine($"- **标签**：{MarkdownText(item.DisplayTags)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.Source))
        {
            target.AppendLine($"- **来源**：{MarkdownText(summary.Source)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.WorkingDirectory))
        {
            target.AppendLine($"- **工作目录**：{MarkdownText(summary.WorkingDirectory)}");
        }

        foreach (var section in summary.Sections)
        {
            target.AppendLine();
            target.AppendLine($"**{MarkdownText(section.Title)}**");
            foreach (var detail in section.Items)
            {
                target.AppendLine($"- {MarkdownText(detail)}");
            }
        }
    }

    private static string MarkdownText(string value)
    {
        return EvidenceSummaryDisplayFormatter.ToPlainText(value)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static string ReminderScheduleDisplay(ReminderScheduleKind kind)
    {
        return kind switch
        {
            ReminderScheduleKind.Daily => "每天",
            ReminderScheduleKind.SpecificWeekdays => "指定工作日",
            ReminderScheduleKind.Once => "单次",
            _ => "工作日"
        };
    }

    private static string OnOff(bool value) => value ? "开启" : "关闭";

    private static int ArrayCount(JsonElement arguments, string name)
    {
        return arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength()
            : 0;
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        return OptionalString(arguments, name)
            ?? throw new AssistantActionException($"动作缺少参数：{name}。");
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        return arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static int RequiredInt(JsonElement arguments, string name)
    {
        return OptionalInt(arguments, name)
            ?? throw new AssistantActionException($"动作缺少参数：{name}。");
    }

    private static int? OptionalInt(JsonElement arguments, string name)
    {
        return arguments.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static bool RequiredBool(JsonElement arguments, string name)
    {
        return OptionalBool(arguments, name)
            ?? throw new AssistantActionException($"动作缺少参数：{name}。");
    }

    private static bool? OptionalBool(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var element))
        {
            return null;
        }
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static List<string> RequiredStringArray(JsonElement arguments, string name)
    {
        var values = OptionalStringArray(arguments, name);
        return values.Count > 0
            ? values
            : throw new AssistantActionException($"动作缺少参数：{name}。");
    }

    private static List<string> OptionalStringArray(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DayOfWeek ParseDayOfWeek(string value)
    {
        if (Enum.TryParse<DayOfWeek>(value, true, out var day))
        {
            return day;
        }
        return value switch
        {
            "周一" => DayOfWeek.Monday,
            "周二" => DayOfWeek.Tuesday,
            "周三" => DayOfWeek.Wednesday,
            "周四" => DayOfWeek.Thursday,
            "周五" => DayOfWeek.Friday,
            "周六" => DayOfWeek.Saturday,
            "周日" or "周天" => DayOfWeek.Sunday,
            _ => throw new AssistantActionException("指定星期值无效。")
        };
    }
}
