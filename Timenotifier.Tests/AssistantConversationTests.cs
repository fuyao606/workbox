using Timenotifier.Models;
using Timenotifier.Services.Assistant;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Settings;
using Xunit;

namespace Timenotifier.Tests;

public sealed class AssistantConversationTests
{
    [Fact]
    public async Task Context_is_minimal_and_never_contains_protected_fields()
    {
        using var fixture = new ConversationFixture();
        var secretApiKey = "api-secret-value";
        var secretPath = Path.Combine(fixture.Root, "secret-screenshots");
        await fixture.Settings.SaveModelSettingsAsync(new ModelProviderSettings
        {
            Analysis = new SingleModelProviderSettings
            {
                BaseUrl = "https://private-model.example",
                ApiKey = secretApiKey,
                Model = "private-model-name"
            }
        });
        await fixture.Settings.SaveEvidenceCaptureSettingsAsync(new EvidenceCaptureSettings
        {
            ScreenshotDirectory = secretPath,
            PrivacyFilters = ["private-window-filter"]
        });
        await fixture.Evidence.UpsertEvidenceAsync(
        [
            new EvidenceItem
            {
                Id = "evidence-safe-id",
                Kind = EvidenceKinds.ManualNote,
                CapturedAt = fixture.Now,
                Title = "AI 助手工作",
                Summary = "完成最小上下文测试",
                WindowTitle = "secret-window-title",
                ProcessName = "secret-process",
                OcrText = "secret-ocr",
                SourcePath = secretPath,
                Included = true
            }
        ]);
        var provider = new AssistantContextProvider(fixture.Settings, fixture.Evidence, fixture.TimeProvider);

        var greeting = await provider.BuildAsync(AssistantModule.Assistant, "你好");
        var evidence = await provider.BuildAsync(AssistantModule.Evidence, "总结今天的证据");

        Assert.DoesNotContain("AI 助手工作", greeting.Text);
        Assert.Contains("AI 助手工作", evidence.Text);
        Assert.Contains("完成最小上下文测试", evidence.Text);
        foreach (var secret in new[]
        {
            secretApiKey,
            "https://private-model.example",
            "private-model-name",
            secretPath,
            "private-window-filter",
            "secret-window-title",
            "secret-process",
            "secret-ocr"
        })
        {
            Assert.DoesNotContain(secret, greeting.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, evidence.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Prompt_contains_protocol_history_and_extracted_text_but_not_source_path()
    {
        using var fixture = new ConversationFixture();
        var session = fixture.Service.CreateTransientSession();
        session.Messages.Add(new AssistantMessage
        {
            Role = AssistantMessageRole.User,
            Text = "上一条问题",
            CreatedAt = fixture.Now
        });
        var secretPath = Path.Combine(fixture.Root, "private", "notes.txt");
        var attachment = new PreparedAssistantAttachment(
            new AssistantAttachmentMetadata
            {
                Name = "notes.txt",
                MediaType = "text/plain",
                SizeBytes = 20
            },
            secretPath,
            "附件安全正文",
            false);

        var prompt = fixture.PromptBuilder.Build(
            session,
            "本次问题",
            AssistantModule.Reports,
            new AssistantContextSnapshot([AssistantModule.Reports], "安全报告上下文"),
            [attachment],
            fixture.Executor.Actions);

        Assert.Contains("\"reply\"", prompt);
        Assert.Contains("最多一个动作", prompt);
        Assert.Contains("上一条问题", prompt);
        Assert.Contains("notes.txt", prompt);
        Assert.Contains("附件安全正文", prompt);
        Assert.DoesNotContain(secretPath, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submit_daily_report", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirmation_action_is_persisted_as_safe_record_without_executing()
    {
        using var fixture = new ConversationFixture(
            """
            {"reply":"我已准备新增提醒。","action":{"name":"upsert_reminder","arguments":{"message":"晚间复盘","time":"18:30","scheduleKind":"Workdays"}}}
            """);
        var session = fixture.Service.CreateTransientSession();

        var result = await fixture.Service.SendAsync(new AssistantTurnRequest(
            session,
            "每天 18:30 提醒我复盘",
            AssistantModule.Reminders,
            []));

        Assert.NotNull(result.Proposal);
        Assert.Equal(AssistantActionRisk.Confirmation, result.Proposal.Risk);
        Assert.Null(result.ImmediateActionResult);
        var settings = await fixture.Settings.LoadReminderSettingsAsync();
        Assert.DoesNotContain(settings.Rules, rule => rule.Message == "晚间复盘");
        var loaded = await fixture.SessionStore.LoadAsync(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(AssistantActionStatus.Proposed, loaded.Messages[1].Action!.Status);
        Assert.Equal("upsert_reminder", loaded.Messages[1].Action!.Name);
    }

    [Fact]
    public async Task Immediate_navigation_executes_locally_and_unknown_action_is_discarded()
    {
        using var navigationFixture = new ConversationFixture(
            """
            {"reply":"打开报告页。","action":{"name":"navigate_module","arguments":{"module":"reports"}}}
            """);
        var navigation = await navigationFixture.Service.SendAsync(new AssistantTurnRequest(
            navigationFixture.Service.CreateTransientSession(),
            "打开报告",
            AssistantModule.Assistant,
            []));

        Assert.Null(navigation.Proposal);
        Assert.Equal(AssistantHostCommandKind.NavigateModule, navigation.ImmediateActionResult!.HostCommand!.Kind);

        using var unknownFixture = new ConversationFixture(
            """
            {"reply":"这个动作不允许。","action":{"name":"submit_daily_report","arguments":{}}}
            """);
        var unknown = await unknownFixture.Service.SendAsync(new AssistantTurnRequest(
            unknownFixture.Service.CreateTransientSession(),
            "直接提交",
            AssistantModule.Tasks,
            []));

        Assert.Null(unknown.Proposal);
        Assert.Null(unknown.ImmediateActionResult);
        Assert.Contains("未授权", unknown.AssistantMessage.Text);
    }

    [Fact]
    public async Task One_active_request_is_enforced_and_cancellation_persists_only_the_user_message()
    {
        using var fixture = new ConversationFixture(response: null, blockModel: true);
        var session = fixture.Service.CreateTransientSession();
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Service.SendAsync(new AssistantTurnRequest(
            session,
            "等待中的请求",
            AssistantModule.Assistant,
            []), cancellation.Token);
        await fixture.Gateway.WaitUntilStartedAsync();

        var busyError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SendAsync(new AssistantTurnRequest(
                session,
                "第二个请求",
                AssistantModule.Assistant,
                [])));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Contains("正在处理", busyError.Message);
        var loaded = await fixture.SessionStore.LoadAsync(session.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Messages);
        Assert.Equal("等待中的请求", loaded.Messages[0].Text);
    }

    [Fact]
    public async Task Production_gateway_loads_its_own_settings_and_conversation_forwards_only_image_inputs()
    {
        using var fixture = new ConversationFixture();
        var gatewaySettings = new CountingSettingsStore(new ModelProviderSettings
        {
            Analysis = new SingleModelProviderSettings
            {
                BaseUrl = "https://gateway-owned.invalid",
                ApiKey = "gateway-owned-key",
                Model = "gateway-owned-model"
            },
            UseVision = true
        });
        var client = new RecordingAssistantModelClient();
        var service = new AssistantConversationService(
            fixture.SessionStore,
            new AssistantContextProvider(fixture.Settings, fixture.Evidence, fixture.TimeProvider),
            fixture.PromptBuilder,
            new AssistantModelGateway(gatewaySettings, client),
            fixture.Executor,
            fixture.TimeProvider);
        var image = new ModelImageInput([0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg", "safe.jpg");
        var attachment = new PreparedAssistantAttachment(
            new AssistantAttachmentMetadata { Name = "safe.jpg", MediaType = "image/jpeg", SizeBytes = 4 },
            "",
            image);

        var result = await service.SendAsync(new AssistantTurnRequest(
            service.CreateTransientSession(),
            "分析图片",
            AssistantModule.Assistant,
            [attachment]));

        Assert.Equal("真实网关回复", result.AssistantMessage.Text);
        Assert.Equal(1, gatewaySettings.ModelLoadCount);
        Assert.Same(gatewaySettings.Settings, client.Settings);
        Assert.Equal([image], client.Images);
    }

    private sealed class ConversationFixture : IDisposable
    {
        public ConversationFixture(string? response = null, bool blockModel = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "Timenotifier.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Now = new DateTime(2026, 7, 10, 10, 0, 0);
            TimeProvider = new FixedTimeProvider(Now);
            var paths = new AppDataPaths(Root);
            Settings = new AppSettingsStore(paths);
            Settings.SaveModelSettingsAsync(new ModelProviderSettings
            {
                Analysis = new SingleModelProviderSettings
                {
                    BaseUrl = "https://model.invalid",
                    ApiKey = "test-key",
                    Model = "test-model"
                }
            }).GetAwaiter().GetResult();
            Evidence = new EvidenceStore(paths);
            var collector = new CompositeEvidenceCollector([], Evidence);
            var modelClient = new StubModelClient();
            var reportGeneration = new ReportGenerationService(
                collector,
                Evidence,
                Settings,
                modelClient,
                new ReportPromptBuilder());
            Executor = new AssistantActionExecutor(
                Settings,
                Evidence,
                collector,
                reportGeneration,
                modelClient,
                TimeProvider);
            SessionStore = new AssistantSessionStore(paths);
            Gateway = new ControllableGateway(response ?? "{\"reply\":\"普通回复\",\"action\":null}", blockModel);
            PromptBuilder = new AssistantPromptBuilder();
            Service = new AssistantConversationService(
                SessionStore,
                new AssistantContextProvider(Settings, Evidence, TimeProvider),
                PromptBuilder,
                Gateway,
                Executor,
                TimeProvider);
        }

        public string Root { get; }
        public DateTime Now { get; }
        public FixedTimeProvider TimeProvider { get; }
        public AppSettingsStore Settings { get; }
        public EvidenceStore Evidence { get; }
        public AssistantSessionStore SessionStore { get; }
        public ControllableGateway Gateway { get; }
        public AssistantPromptBuilder PromptBuilder { get; }
        public AssistantActionExecutor Executor { get; }
        public AssistantConversationService Service { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now => now;
    }

    private sealed class ControllableGateway(string response, bool blockModel) : IAssistantModelGateway
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync() => _started.Task;

        public async Task<string> CompleteAsync(
            string prompt,
            IReadOnlyList<ModelImageInput> images,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            if (blockModel)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return response;
        }
    }

    private sealed class RecordingAssistantModelClient : IAssistantModelClient
    {
        public ModelProviderSettings? Settings { get; private set; }
        public IReadOnlyList<ModelImageInput> Images { get; private set; } = [];

        public Task<string> GenerateAssistantAsync(
            ModelProviderSettings settings,
            string prompt,
            IReadOnlyList<ModelImageInput> images,
            CancellationToken cancellationToken)
        {
            Settings = settings;
            Images = images;
            return Task.FromResult("{\"reply\":\"真实网关回复\",\"action\":null}");
        }
    }

    private sealed class CountingSettingsStore(ModelProviderSettings settings) : IAppSettingsStore
    {
        public ModelProviderSettings Settings { get; } = settings;
        public int ModelLoadCount { get; private set; }

        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            ModelLoadCount++;
            return Task.FromResult(Settings);
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ReminderSettings());
        public Task SaveReminderSettingsAsync(ReminderSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReportTemplate>>([]);
        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EvidenceCaptureSettings());
        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DailyReportAutomationSettings());
        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubModelClient : IModelClient
    {
        public Task<string> GenerateAsync(ModelProviderSettings settings, string prompt, IReadOnlyList<EvidenceItem> evidence, CancellationToken cancellationToken = default)
            => Task.FromResult("report");

        public Task<ModelConnectionTestResult> TestConnectionAsync(SingleModelProviderSettings provider, ModelConnectionTestKind kind, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelConnectionTestResult(true, "ok", "", false));

        public Task<string> PolishReportAsync(ModelProviderSettings settings, string draft, IReadOnlyList<EvidenceItem> evidence, CancellationToken cancellationToken = default)
            => Task.FromResult(draft);
    }
}
