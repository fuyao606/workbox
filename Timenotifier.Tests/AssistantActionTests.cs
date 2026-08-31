using System.Text.Json;
using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantActionTests
{
    private static readonly string[] ImmediateActions =
    [
        "navigate_module",
        "get_app_overview",
        "list_reminders",
        "get_model_status",
        "list_evidence",
        "get_review_summary",
        "list_report_drafts",
        "list_report_templates",
        "get_capture_status",
        "get_automation_status",
        "preview_evidence_image"
    ];

    private static readonly string[] ConfirmationActions =
    [
        "upsert_reminder",
        "delete_reminders",
        "set_snooze_minutes",
        "upsert_holiday_adjustment",
        "delete_holiday_adjustments",
        "set_model_preferences",
        "test_model_connection",
        "refresh_evidence",
        "set_evidence_included",
        "delete_evidence",
        "generate_report",
        "load_report_draft",
        "save_current_report",
        "confirm_current_report",
        "delete_report_drafts",
        "new_draft_from_confirmed",
        "copy_current_report",
        "export_weekly_report",
        "set_capture_preferences",
        "set_automation_preferences",
        "open_automation_login",
        "run_daily_automation"
    ];

    [Fact]
    public void Catalog_has_explicit_risk_for_every_approved_action_and_rejects_submit()
    {
        using var fixture = new ActionFixture();

        Assert.All(ImmediateActions, name =>
            Assert.Equal(AssistantActionRisk.Immediate, fixture.Executor.GetRisk(name)));
        Assert.All(ConfirmationActions, name =>
            Assert.Equal(AssistantActionRisk.Confirmation, fixture.Executor.GetRisk(name)));
        Assert.Null(fixture.Executor.GetRisk("submit_daily_report"));
        Assert.Null(fixture.Executor.GetRisk("arbitrary_shell"));
    }

    [Fact]
    public void Proposal_rejects_sensitive_properties_and_more_than_twenty_targets()
    {
        using var fixture = new ActionFixture();
        var sensitive = Parsed("set_model_preferences", """{"qualityMode":"Balanced","apiKey":"secret"}""");
        var tooMany = Parsed(
            "delete_evidence",
            JsonSerializer.Serialize(new { ids = Enumerable.Range(1, 21).Select(index => $"id-{index}").ToArray() }));

        var sensitiveError = Assert.Throws<AssistantActionException>(() => fixture.Executor.CreateProposal(sensitive));
        var countError = Assert.Throws<AssistantActionException>(() => fixture.Executor.CreateProposal(tooMany));

        Assert.Contains("敏感", sensitiveError.Message);
        Assert.Contains("20", countError.Message);
    }

    [Fact]
    public async Task Reminder_proposal_has_no_side_effect_until_execute_then_saves_and_requests_reload()
    {
        using var fixture = new ActionFixture();
        var before = await fixture.Settings.LoadReminderSettingsAsync();
        var proposal = fixture.Executor.CreateProposal(Parsed(
            "upsert_reminder",
            """
            {"message":"提交代码评审","time":"18:20","scheduleKind":"Workdays","advanceMinutes":5}
            """));

        var unchanged = await fixture.Settings.LoadReminderSettingsAsync();
        Assert.Equal(before.Rules.Count, unchanged.Rules.Count);
        Assert.Equal(AssistantActionRisk.Confirmation, proposal.Risk);

        var result = await fixture.Executor.ExecuteAsync(proposal);
        var saved = await fixture.Settings.LoadReminderSettingsAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.RefreshTargets.HasFlag(AssistantRefreshTargets.Reminders));
        var reminder = Assert.Single(saved.Rules.Where(rule => rule.Message == "提交代码评审"));
        Assert.Equal(new TimeOnly(18, 20), reminder.Time);
        Assert.Equal(5, reminder.AdvanceMinutes);
    }

    [Fact]
    public async Task Capture_preferences_preserve_sensitive_path_and_privacy_filters()
    {
        using var fixture = new ActionFixture();
        await fixture.Settings.SaveEvidenceCaptureSettingsAsync(new EvidenceCaptureSettings
        {
            ScreenshotDirectory = fixture.SecretPath,
            PrivacyFilters = ["private-window"],
            DefaultTags = ["old"]
        });
        var proposal = fixture.Executor.CreateProposal(Parsed(
            "set_capture_preferences",
            """
            {"autoScreenshotEnabled":true,"screenshotIntervalMinutes":15,"maxAutoScreenshots":80,"manualNoteHotkey":"Ctrl+Shift+N","startWithWindows":true,"ocrEnabled":true,"defaultTags":["AI","研发"]}
            """));

        var result = await fixture.Executor.ExecuteAsync(proposal);
        var saved = await fixture.Settings.LoadEvidenceCaptureSettingsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(fixture.SecretPath, saved.ScreenshotDirectory);
        Assert.Equal(["private-window"], saved.PrivacyFilters);
        Assert.Equal(["AI", "研发"], saved.DefaultTags);
        Assert.Equal(15, saved.ScreenshotIntervalMinutes);
        Assert.True(result.RefreshTargets.HasFlag(AssistantRefreshTargets.Capture));
    }

    [Fact]
    public async Task Automation_preferences_preserve_url_session_and_profile()
    {
        using var fixture = new ActionFixture();
        var original = new DailyReportAutomationSettings
        {
            TargetUrl = "https://private.example/report",
            BrowserSessionName = "private-session",
            BrowserProfileDirectory = fixture.SecretPath
        };
        await fixture.Settings.SaveDailyReportAutomationSettingsAsync(original);
        var proposal = fixture.Executor.CreateProposal(Parsed(
            "set_automation_preferences",
            """
            {"isEnabled":true,"runTime":"18:05","projectName":"AI 平台","taskName":"助手首页","taskType":"开发","workloadPercent":80}
            """));

        var result = await fixture.Executor.ExecuteAsync(proposal);
        var saved = await fixture.Settings.LoadDailyReportAutomationSettingsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(original.TargetUrl, saved.TargetUrl);
        Assert.Equal(original.BrowserSessionName, saved.BrowserSessionName);
        Assert.Equal(original.BrowserProfileDirectory, saved.BrowserProfileDirectory);
        Assert.Equal(new TimeOnly(18, 5), saved.RunTime);
        Assert.Equal(80, saved.WorkloadPercent);
        Assert.True(result.RefreshTargets.HasFlag(AssistantRefreshTargets.Automation));
    }

    [Fact]
    public async Task Navigation_and_automation_actions_return_host_commands_without_external_execution()
    {
        using var fixture = new ActionFixture();
        var navigation = fixture.Executor.CreateProposal(Parsed(
            "navigate_module",
            """{"module":"reports"}"""));
        var automation = fixture.Executor.CreateProposal(Parsed(
            "run_daily_automation",
            "{}"));

        var navigationResult = await fixture.Executor.ExecuteAsync(navigation);
        var automationResult = await fixture.Executor.ExecuteAsync(automation);

        Assert.Equal(AssistantHostCommandKind.NavigateModule, navigationResult.HostCommand!.Kind);
        Assert.Equal(AssistantModule.Reports, navigationResult.HostCommand.Module);
        Assert.Equal(AssistantHostCommandKind.RunDailyAutomation, automationResult.HostCommand!.Kind);
        Assert.Equal(0, fixture.Model.GenerateCount);
    }

    [Fact]
    public async Task List_evidence_returns_structured_markdown_without_internal_identifiers()
    {
        using var fixture = new ActionFixture();
        var evidence = new EvidenceItem
        {
            Id = "codex:internal-session-id",
            Kind = EvidenceKinds.CodexSession,
            CapturedAt = new DateTime(2026, 7, 10, 9, 26, 0),
            Title = "优化配置中心回顾输出",
            Summary = "来源：Codex\n工作目录：D:\\Code\\workbox\n目标概览：1. 修复回顾展示\n工作结果概括：1. 已完成格式化输出",
            Tags = ["开发"],
            Included = true
        };
        await fixture.EvidenceStore.UpsertEvidenceAsync([evidence]);
        var proposal = fixture.Executor.CreateProposal(Parsed("list_evidence", "{\"range\":\"today\"}"));

        var result = await fixture.Executor.ExecuteAsync(proposal);

        Assert.Contains("### 证据列表", result.Message);
        Assert.Contains("#### 07-10 09:26 · Codex", result.Message);
        Assert.Contains("**目标概览**", result.Message);
        Assert.Contains("- 修复回顾展示", result.Message);
        Assert.DoesNotContain(evidence.Id, result.Message);
    }

    private static AssistantParsedAction Parsed(string name, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return new AssistantParsedAction(name, document.RootElement.Clone());
    }

    private sealed class ActionFixture : IDisposable
    {
        private readonly string _root;

        public ActionFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "Timenotifier.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            SecretPath = Path.Combine(_root, "private");
            var paths = new AppDataPaths(_root);
            Settings = new AppSettingsStore(paths);
            EvidenceStore = new EvidenceStore(paths);
            var collector = new CompositeEvidenceCollector([], EvidenceStore);
            Model = new CountingModelClient();
            var reportGeneration = new ReportGenerationService(
                collector,
                EvidenceStore,
                Settings,
                Model,
                new ReportPromptBuilder());
            Executor = new AssistantActionExecutor(
                Settings,
                EvidenceStore,
                collector,
                reportGeneration,
                Model,
                new FixedTimeProvider(new DateTime(2026, 7, 10, 10, 0, 0)));
        }

        public string SecretPath { get; }
        public AppSettingsStore Settings { get; }
        public EvidenceStore EvidenceStore { get; }
        public CountingModelClient Model { get; }
        public AssistantActionExecutor Executor { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now => now;
    }

    private sealed class CountingModelClient : IModelClient
    {
        public int GenerateCount { get; private set; }

        public Task<string> GenerateAsync(
            ModelProviderSettings settings,
            string prompt,
            IReadOnlyList<EvidenceItem> evidence,
            CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            return Task.FromResult("## 今日工作内容简述\n1、助手测试报告");
        }

        public Task<ModelConnectionTestResult> TestConnectionAsync(
            SingleModelProviderSettings provider,
            ModelConnectionTestKind kind,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelConnectionTestResult(true, "连接成功", "https://redacted.invalid", false));
        }

        public Task<string> PolishReportAsync(
            ModelProviderSettings settings,
            string draft,
            IReadOnlyList<EvidenceItem> evidence,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(draft);
        }
    }
}
