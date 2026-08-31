﻿﻿﻿﻿﻿﻿using Timenotifier.Services.Assistant;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Reporting;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;

namespace Timenotifier.Application;

public sealed class AppBootstrapper
{
    public AppServices Bootstrap(
        string? dataRoot,
        Action<string, string> showReminder,
        Func<Task> runDailyReportAutomationAsync,
        string? assistantFixture = null)
    {
        var paths = new AppDataPaths(dataRoot);
        paths.EnsureCreated();

        var evidenceStore = new EvidenceStore(paths);
        var settingsStore = new AppSettingsStore(paths);
        var activeWindowService = new ActiveWindowService();
        var screenshotCapture = new ScreenshotCaptureService(paths, evidenceStore, SystemTimeProvider.Instance, activeWindowService);
        var manualEvidence = new ManualEvidenceService(evidenceStore, SystemTimeProvider.Instance, activeWindowService);
        var autoScreenshotScheduler = new AutoScreenshotScheduler(settingsStore, screenshotCapture, message => showReminder(message, "证据采集"));
        var evidenceCollector = new CompositeEvidenceCollector(
            [
                new CodexSessionEvidenceCollector(new CodexSessionEvidenceReader(
                    CodexSessionEvidenceReader.CreateDefaultSources())),
                new TraeCnAiAgentEvidenceCollector(new TraeCnAiAgentEvidenceReader()),
                new DeepSeekHarnessEvidenceCollector(new DeepSeekHarnessEvidenceReader())
            ],
            evidenceStore);
        var modelClient = new ResponsesModelClient();
        var reportGeneration = new ReportGenerationService(
            evidenceCollector,
            evidenceStore,
            settingsStore,
            modelClient,
            new ReportPromptBuilder());
        var dailyReportAutomation = new DailyReportAutomationService(
            settingsStore,
            evidenceStore,
            reportGeneration,
            new PlaywrightCliAutomationRunner(paths),
            paths,
            SystemTimeProvider.Instance);
        var dailyReportAutomationScheduler = new DailyReportAutomationScheduler(
            settingsStore,
            SystemTimeProvider.Instance,
            runDailyReportAutomationAsync);
        var assistantSessionStore = new AssistantSessionStore(paths);
        var assistantAttachmentService = new AssistantAttachmentService();
        var assistantActionExecutor = new AssistantActionExecutor(
            settingsStore,
            evidenceStore,
            evidenceCollector,
            reportGeneration,
            modelClient,
            SystemTimeProvider.Instance);
        IAssistantModelGateway assistantModelGateway = string.Equals(
            assistantFixture,
            "basic",
            StringComparison.OrdinalIgnoreCase)
            ? new DeterministicAssistantModelGateway()
            : new AssistantModelGateway(settingsStore, modelClient);
        var assistantConversation = new AssistantConversationService(
            assistantSessionStore,
            new AssistantContextProvider(settingsStore, evidenceStore, SystemTimeProvider.Instance),
            new AssistantPromptBuilder(),
            assistantModelGateway,
            assistantActionExecutor,
            SystemTimeProvider.Instance);

        return new AppServices(
            paths,
            evidenceStore,
            settingsStore,
            activeWindowService,
            screenshotCapture,
            manualEvidence,
            autoScreenshotScheduler,
            evidenceCollector,
            reportGeneration,
            dailyReportAutomation,
            dailyReportAutomationScheduler,
            assistantConversation,
            assistantSessionStore,
            assistantAttachmentService);
    }
}

public sealed record AppServices(
    AppDataPaths Paths,
    IEvidenceStore EvidenceStore,
    IAppSettingsStore SettingsStore,
    IActiveWindowService ActiveWindowService,
    IScreenshotCaptureService ScreenshotCapture,
    ManualEvidenceService ManualEvidence,
    AutoScreenshotScheduler AutoScreenshotScheduler,
    CompositeEvidenceCollector EvidenceCollector,
    ReportGenerationService ReportGeneration,
    DailyReportAutomationService DailyReportAutomation,
    DailyReportAutomationScheduler DailyReportAutomationScheduler,
    IAssistantConversationService AssistantConversation,
    IAssistantSessionStore AssistantSessionStore,
    IAssistantAttachmentService AssistantAttachmentService);
