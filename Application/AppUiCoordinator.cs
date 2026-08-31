using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Services.Automation;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Views;

namespace Timenotifier.Application;

public sealed class AppUiCoordinator(
    AppServices services,
    Dispatcher dispatcher,
    bool isUiLoopMode)
{
    private MainWindow? _floatingBall;
    private ConfigurationWindow? _configurationWindow;
    private ManualEvidenceHotkeyService? _hotkeyService;
    private bool _captureServicesInitialized;

    public ManualEvidenceHotkeyService? HotkeyService => _hotkeyService;

    public void AttachLifetimeCoordinator(AppLifetimeCoordinator lifetimeCoordinator)
    {
        LifetimeCoordinator = lifetimeCoordinator;
    }

    public AppLifetimeCoordinator? LifetimeCoordinator { get; private set; }

    public void OpenConfiguration()
    {
        OpenConfiguration(ConfigurationSection.Assistant);
    }

    public void OpenConfiguration(ConfigurationSection section)
    {
        if (_configurationWindow is not null)
        {
            _configurationWindow.SelectSection(section);
            BringConfigurationToFront(_configurationWindow);
            return;
        }

        _configurationWindow = new ConfigurationWindow(
            services.SettingsStore,
            services.EvidenceStore,
            services.EvidenceCollector,
            services.ReportGeneration,
            services.DailyReportAutomation,
            services.AssistantConversation,
            services.AssistantSessionStore,
            services.AssistantAttachmentService,
            SystemTimeProvider.Instance,
            () => LifetimeCoordinator?.ReminderScheduler?.ReloadSettingsAsync() ?? Task.CompletedTask,
            ReloadCaptureServicesAsync,
            () => services.DailyReportAutomationScheduler.ReloadAsync(),
            StartDailyReportAutomationAsync,
            OpenDailyReportLoginBrowserAsync,
            section);
        if (!isUiLoopMode && _floatingBall?.IsVisible == true)
        {
            _configurationWindow.Owner = _floatingBall;
        }

        _configurationWindow.Closed += (_, _) => _configurationWindow = null;
        BringConfigurationToFront(_configurationWindow);
    }

    private static void BringConfigurationToFront(ConfigurationWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            window.WindowState = System.Windows.WindowState.Normal;
        }

        window.Activate();
    }

    public MainWindow CreateFloatingBall()
    {
        var floatingBall = new MainWindow();
        floatingBall.ConfigurationRequested += (_, _) => OpenConfiguration(ConfigurationSection.Assistant);
        floatingBall.ScreenshotRequested += async (_, _) => await CaptureScreenshotAsync();
        _floatingBall = floatingBall;
        return floatingBall;
    }

    public async Task InitializeCaptureServicesAsync()
    {
        if (_captureServicesInitialized)
        {
            return;
        }

        _captureServicesInitialized = true;
        await services.AutoScreenshotScheduler.StartAsync();
        await RegisterManualHotkeyAsync();
    }

    private void ShowReminder(string message, string title = "时间提醒")
    {
        LifetimeCoordinator?.ShowReminder(message, title);
    }

    private async Task CaptureScreenshotAsync()
    {
        var shouldRestoreFloatingBall = _floatingBall?.IsVisible == true;
        EvidenceItem? evidence = null;
        try
        {
            var settings = await services.SettingsStore.LoadEvidenceCaptureSettingsAsync();
            if (shouldRestoreFloatingBall)
            {
                _floatingBall!.Hide();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await Task.Delay(150);
            }

            evidence = await services.ScreenshotCapture.CaptureFullScreenAsync(settings, isAutoCaptured: false)
                ?? throw new InvalidOperationException("截图未保存。");
            if (shouldRestoreFloatingBall)
            {
                _floatingBall!.Show();
            }

            var window = new ManualEvidenceWindow(
                settings.DefaultTags,
                "记录这张截图里的工作",
                "这张截图记录了什么工作？");
            if (_floatingBall is not null)
            {
                window.Owner = _floatingBall;
            }

            if (window.ShowDialog() != true)
            {
                await services.EvidenceStore.DeleteEvidenceAsync(evidence.Id);
                ShowReminder("已取消截图记录。", "截图记录");
                return;
            }

            ApplyScreenshotNote(evidence, window.ContentText, window.Tags);
            await services.EvidenceStore.UpsertEvidenceAsync([evidence]);
            ShowReminder($"已记录截图证据：{evidence.CapturedAt:HH:mm:ss}", "截图记录");
        }
        catch (Exception ex)
        {
            if (shouldRestoreFloatingBall)
            {
                _floatingBall!.Show();
            }

            if (evidence is not null)
            {
                await services.EvidenceStore.DeleteEvidenceAsync(evidence.Id);
            }

            ShowReminder($"保存失败：{ex.Message}", "截图记录");
        }
    }

    private async Task ReloadCaptureServicesAsync()
    {
        await services.AutoScreenshotScheduler.ReloadAsync();
        await RegisterManualHotkeyAsync();
    }

    private async Task RegisterManualHotkeyAsync()
    {
        if (_floatingBall is null)
        {
            return;
        }

        if (isUiLoopMode)
        {
            return;
        }

        var settings = await services.SettingsStore.LoadEvidenceCaptureSettingsAsync();
        _hotkeyService ??= new ManualEvidenceHotkeyService(() => _ = CaptureScreenshotAsync());
        var registered = _hotkeyService.Register(settings.ManualNoteHotkey, new System.Windows.Interop.WindowInteropHelper(_floatingBall));
        if (!registered)
        {
            ShowReminder($"快捷键注册失败：{settings.ManualNoteHotkey}", "证据采集");
        }
    }

    private async Task OpenManualEvidenceAsync()
    {
        var settings = await services.SettingsStore.LoadEvidenceCaptureSettingsAsync();
        var window = new ManualEvidenceWindow(settings.DefaultTags);
        if (_floatingBall is not null)
        {
            window.Owner = _floatingBall;
        }

        if (window.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var evidence = await services.ManualEvidence.SaveNoteAsync(window.ContentText, window.Tags);
            ShowReminder($"已记录：{evidence.CapturedAt:HH:mm:ss}", "手动记录");
        }
        catch (Exception ex)
        {
            ShowReminder($"记录失败：{ex.Message}", "手动记录");
        }
    }

    internal static void ApplyScreenshotNote(
        EvidenceItem evidence,
        string content,
        IReadOnlyList<string> tags)
    {
        var note = content.Trim();
        if (!string.IsNullOrWhiteSpace(note))
        {
            var originalSummary = evidence.Summary.Trim();
            evidence.Summary = string.IsNullOrWhiteSpace(originalSummary)
                ? $"工作记录：{note}"
                : $"工作记录：{note}{Environment.NewLine}{originalSummary}";

            var firstLine = note
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                evidence.Title = firstLine.Length <= 60 ? firstLine : firstLine[..60] + "...";
            }
        }

        evidence.Tags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<bool> StartDailyReportAutomationAsync()
    {
        try
        {
            var result = await services.DailyReportAutomation.PrepareAsync();
            var window = new AutomationReviewWindow(services.DailyReportAutomation, result);
            if (_floatingBall is not null)
            {
                window.Owner = _floatingBall;
            }

            window.Show();
            window.Activate();
            return true;
        }
        catch (Exception ex)
        {
            ShowReminder($"日报自动化失败：{ResponsesModelClient.FlattenException(ex)}", "任务自动化");
            return false;
        }
    }

    private async Task<bool> OpenDailyReportLoginBrowserAsync()
    {
        try
        {
            await services.DailyReportAutomation.OpenLoginBrowserAsync();
            ShowReminder("已打开日报系统，请先完成登录。", "任务自动化");
            return true;
        }
        catch (Exception ex)
        {
            ShowReminder($"打开登录浏览器失败：{ResponsesModelClient.FlattenException(ex)}", "任务自动化");
            return false;
        }
    }
}
