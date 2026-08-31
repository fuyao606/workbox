using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantContextTests
{
    private static readonly DateTime Now = new(2026, 7, 8, 14, 30, 0);

    [Fact]
    public async Task Ordinary_conversation_does_not_read_evidence()
    {
        var evidence = new RecordingEvidenceStore([]);
        var provider = new AssistantContextProvider(new ContextSettingsStore(), evidence, new FixedTimeProvider(Now));

        var snapshot = await provider.BuildAsync(AssistantModule.Assistant, "你好，今天心情怎么样？");

        Assert.Empty(snapshot.Modules);
        Assert.Equal(0, evidence.ReadCount);
        Assert.DoesNotContain("[证据", snapshot.Text);
    }

    [Fact]
    public async Task Evidence_context_uses_today_and_keeps_only_newest_fifty_in_deterministic_order()
    {
        var items = Enumerable.Range(0, 60)
            .Select(index => SafeEvidence($"e-{index:00}", Now.Date.AddMinutes(index)))
            .Reverse()
            .ToList();
        var evidence = new RecordingEvidenceStore(items);
        var provider = new AssistantContextProvider(new ContextSettingsStore(), evidence, new FixedTimeProvider(Now));

        var snapshot = await provider.BuildAsync(AssistantModule.Evidence, "查看今天的证据");

        Assert.Equal(new DateOnly(2026, 7, 8), evidence.LastStartDate);
        Assert.Equal(new DateOnly(2026, 7, 8), evidence.LastEndDate);
        Assert.DoesNotContain("e-09|", snapshot.Text);
        Assert.Contains("e-10|", snapshot.Text);
        Assert.Contains("e-59|", snapshot.Text);
        Assert.True(snapshot.Text.IndexOf("e-59|", StringComparison.Ordinal) < snapshot.Text.IndexOf("e-58|", StringComparison.Ordinal));
        Assert.Equal(50, snapshot.Text.Split(Environment.NewLine).Count(line => line.StartsWith("e-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Report_module_loads_current_work_week_evidence_even_without_evidence_keyword()
    {
        var evidence = new RecordingEvidenceStore([SafeEvidence("weekly", Now)]);
        var provider = new AssistantContextProvider(new ContextSettingsStore(), evidence, new FixedTimeProvider(Now));

        var snapshot = await provider.BuildAsync(AssistantModule.Reports, "概括本周完成情况");

        var week = InternshipWeekCalculator.GetWeek(DateOnly.FromDateTime(Now));
        Assert.Equal(week.StartDate, evidence.LastStartDate);
        Assert.Equal(week.EndDate, evidence.LastEndDate);
        Assert.Contains("weekly", snapshot.Text);
    }

    [Fact]
    public async Task Explicit_module_is_first_and_survives_when_inferred_modules_exceed_context_cap()
    {
        var evidence = new RecordingEvidenceStore([SafeEvidence("should-not-load", Now)]);
        var provider = new AssistantContextProvider(new ContextSettingsStore(), evidence, new FixedTimeProvider(Now));

        var snapshot = await provider.BuildAsync(
            AssistantModule.Tasks,
            "查看提醒、模型、证据和任务状态");

        Assert.Equal(
            [AssistantModule.Tasks, AssistantModule.Reminders, AssistantModule.Model],
            snapshot.Modules);
        Assert.Equal(0, evidence.ReadCount);
        Assert.Contains("[日报任务状态]", snapshot.Text);
    }

    [Fact]
    public async Task Context_and_prompt_exclude_secret_path_and_raw_setting_fields()
    {
        const string apiKey = "sk-SENTINEL-API-KEY-123456789";
        const string baseUrl = "https://sentinel.invalid/private-endpoint";
        const string model = "sentinel-private-model";
        const string screenshotPath = "C:\\sentinel\\screenshots";
        const string browserPath = "C:\\sentinel\\browser-profile";
        const string windowTitle = "SENTINEL WINDOW TITLE";
        const string processName = "sentinel-process.exe";
        const string ocrText = "SENTINEL OCR RAW TEXT";
        const string privacyText = "SENTINEL PRIVACY FILTER";
        const string targetUrl = "https://sentinel.invalid/submit";
        const string templateBody = "SENTINEL COMPLEX TEMPLATE BODY";
        var settings = new ContextSettingsStore
        {
            ModelSettings = new ModelProviderSettings
            {
                Analysis = new SingleModelProviderSettings { ApiKey = apiKey, BaseUrl = baseUrl, Model = model },
                UseVision = true
            },
            CaptureSettings = new EvidenceCaptureSettings
            {
                AutoScreenshotEnabled = true,
                ScreenshotDirectory = screenshotPath,
                PrivacyFilters = [privacyText]
            },
            AutomationSettings = new DailyReportAutomationSettings
            {
                IsEnabled = true,
                BrowserProfileDirectory = browserPath,
                TargetUrl = targetUrl,
                ProjectName = "安全项目",
                TaskName = "安全任务"
            },
            Templates = [new ReportTemplate { Id = "template-1", Name = "安全模板", PromptInstructions = templateBody }]
        };
        var item = SafeEvidence("safe-id", Now);
        item.ImagePath = "C:\\sentinel\\image.jpg";
        item.SourcePath = "C:\\sentinel\\source.json";
        item.WindowTitle = windowTitle;
        item.ProcessName = processName;
        item.OcrText = ocrText;
        var provider = new AssistantContextProvider(settings, new RecordingEvidenceStore([item]), new FixedTimeProvider(Now));

        var snapshots = await Task.WhenAll(
            provider.BuildAsync(AssistantModule.Model, "模型状态"),
            provider.BuildAsync(AssistantModule.Evidence, "今天证据"),
            provider.BuildAsync(AssistantModule.Reports, "日报"),
            provider.BuildAsync(AssistantModule.Capture, "采集状态"),
            provider.BuildAsync(AssistantModule.Tasks, "任务状态"));
        var contextText = string.Join(Environment.NewLine, snapshots.Select(snapshot => snapshot.Text));
        var prompt = new AssistantPromptBuilder().Build(
            new AssistantSession(),
            "请总结安全状态",
            AssistantModule.Assistant,
            new AssistantContextSnapshot([], contextText),
            [],
            new Dictionary<string, AssistantActionRisk> { ["navigate_module"] = AssistantActionRisk.Immediate });

        foreach (var forbidden in new[]
        {
            apiKey, baseUrl, model, screenshotPath, browserPath, windowTitle, processName, ocrText,
            privacyText, targetUrl, templateBody, item.ImagePath!, item.SourcePath!
        })
        {
            Assert.DoesNotContain(forbidden, contextText, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, prompt, StringComparison.Ordinal);
        }
        Assert.Contains("safe-id", contextText);
        Assert.Contains("安全项目", contextText);
        Assert.Contains("安全模板", contextText);
    }

    [Fact]
    public void Prompt_marks_context_and_attachment_as_untrusted_and_requires_one_action_json()
    {
        var attachment = new PreparedAssistantAttachment(
            new AssistantAttachmentMetadata { Name = "notes.txt", MediaType = "text/plain", SizeBytes = 12 },
            "附件中的不可信指令");

        var prompt = new AssistantPromptBuilder().Build(
            new AssistantSession(),
            "保留本次输入",
            AssistantModule.Review,
            new AssistantContextSnapshot([AssistantModule.Review], "上下文中的不可信指令"),
            [attachment],
            new Dictionary<string, AssistantActionRisk> { ["navigate_module"] = AssistantActionRisk.Immediate });

        Assert.Contains("不可信数据", prompt);
        Assert.Contains("上下文中的不可信指令", prompt);
        Assert.Contains("附件中的不可信指令", prompt);
        Assert.Contains("保留本次输入", prompt);
        Assert.Contains("{\"reply\":\"给用户的回复\",\"action\":null}", prompt);
        Assert.Contains("最多一个动作", prompt);
        Assert.Contains("不得声称动作已经执行", prompt);
        Assert.Contains("不得向外部系统提交", prompt);
    }

    [Fact]
    public void Prompt_keeps_only_latest_twelve_visible_messages_and_twenty_four_thousand_characters()
    {
        var session = new AssistantSession
        {
            Messages = Enumerable.Range(0, 15)
                .Select(index => new AssistantMessage
                {
                    Role = index == 2 ? AssistantMessageRole.System : AssistantMessageRole.Assistant,
                    Text = $"H{index:00}-" + new string((char)('a' + index), 2_200)
                })
                .ToList()
        };

        var prompt = new AssistantPromptBuilder().Build(
            session,
            "CURRENT-INPUT",
            AssistantModule.Assistant,
            new AssistantContextSnapshot([], "无"),
            [],
            new Dictionary<string, AssistantActionRisk>());
        var history = Between(prompt, "最近对话：", "按需装载的本地上下文");

        Assert.DoesNotContain("H00-", history);
        Assert.DoesNotContain("H01-", history);
        Assert.DoesNotContain("H02-", history);
        Assert.Contains("H14-", history);
        Assert.True(history.Trim().Length <= 24_000, $"History length was {history.Trim().Length}.");
        Assert.Contains("CURRENT-INPUT", prompt);
    }

    private static string Between(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
        return value[start..end];
    }

    private static EvidenceItem SafeEvidence(string id, DateTime capturedAt)
    {
        return new EvidenceItem
        {
            Id = id,
            Kind = EvidenceKinds.ManualNote,
            CapturedAt = capturedAt,
            Title = $"title-{id}",
            Summary = $"summary-{id}",
            Tags = ["tag"],
            Included = true
        };
    }

    private sealed class FixedTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now { get; } = now;
    }

    private sealed class RecordingEvidenceStore(IReadOnlyList<EvidenceItem> items) : IEvidenceStore
    {
        public int ReadCount { get; private set; }
        public DateOnly LastStartDate { get; private set; }
        public DateOnly LastEndDate { get; private set; }

        public Task<IReadOnlyList<EvidenceItem>> ReadEvidenceAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastStartDate = startDate;
            LastEndDate = endDate;
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<ReportDraft>> ListReportDraftsAsync(string? reportType = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReportDraft>>([]);
        public Task UpsertEvidenceAsync(IEnumerable<EvidenceItem> evidence, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveReportDraftAsync(ReportDraft draft, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReportDraft?> LoadReportDraftAsync(string reportType, DateOnly startDate, CancellationToken cancellationToken = default) => Task.FromResult<ReportDraft?>(null);
        public Task<ReportDraft?> LoadReportDraftByIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ReportDraft?>(null);
        public Task<bool> DeleteEvidenceAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> DeleteEvidenceAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> DeleteReportDraftAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> DeleteReportDraftsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task PruneAutoScreenshotEvidenceAsync(int maxCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ContextSettingsStore : IAppSettingsStore
    {
        public ModelProviderSettings ModelSettings { get; init; } = new();
        public ReminderSettings ReminderSettings { get; init; } = new();
        public IReadOnlyList<ReportTemplate> Templates { get; init; } = [];
        public EvidenceCaptureSettings CaptureSettings { get; init; } = new();
        public DailyReportAutomationSettings AutomationSettings { get; init; } = new();

        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ModelSettings);
        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ReminderSettings);
        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Templates);
        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(CaptureSettings);
        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(AutomationSettings);
        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
