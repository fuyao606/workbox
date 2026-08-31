using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Views;
using WpfApplication = global::System.Windows.Application;
using Timenotifier.Services.Evidence;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Scheduling;

namespace Timenotifier.Application;

public sealed class AppLifetimeCoordinator(
    AppServices services,
    Dispatcher dispatcher,
    Func<MainWindow> createFloatingBall,
    Func<Task> initializeCaptureServicesAsync,
    Action<ConfigurationSection> openConfiguration)
{
    private TrayIconService? _trayIcon;
    private ReminderScheduler? _reminderScheduler;

    public MainWindow? FloatingBall { get; private set; }

    public ReminderScheduler? ReminderScheduler => _reminderScheduler;

    public void Start(bool openConfigurationOnStartup, ConfigurationSection startupSection)
    {
        var floatingBall = EnsureFloatingBall();
        floatingBall.Show();
        _ = initializeCaptureServicesAsync();

        _trayIcon = new TrayIconService(
            showFloatingBall: ShowFloatingBall,
            hideFloatingBall: HideFloatingBall,
            isFloatingBallVisible: IsFloatingBallVisible,
            openConfiguration: () => openConfiguration(ConfigurationSection.Reminders),
            exitApplication: WpfApplication.Current.Shutdown);
        _trayIcon.Show();

        _reminderScheduler = new ReminderScheduler(
            SystemTimeProvider.Instance,
            services.SettingsStore,
            ShowReminder);
        _ = _reminderScheduler.StartAsync();
        _ = services.DailyReportAutomationScheduler.StartAsync();

        if (openConfigurationOnStartup)
        {
            dispatcher.BeginInvoke(() => openConfiguration(startupSection), DispatcherPriority.ApplicationIdle);
        }
    }

    public void Stop(ManualEvidenceHotkeyService? hotkeyService)
    {
        _reminderScheduler?.Stop();
        services.AutoScreenshotScheduler.Stop();
        services.DailyReportAutomationScheduler.Stop();
        hotkeyService?.Dispose();
        _trayIcon?.Dispose();
    }

    public MainWindow EnsureFloatingBall()
    {
        FloatingBall ??= createFloatingBall();
        return FloatingBall;
    }

    public void ShowFloatingBall()
    {
        EnsureFloatingBall().ShowExpanded();
    }

    public void HideFloatingBall()
    {
        FloatingBall?.Hide();
    }

    public bool IsFloatingBallVisible()
    {
        return FloatingBall?.IsVisible == true;
    }

    private void ShowReminder(ReminderOccurrence occurrence)
    {
        var title = occurrence.IsSnoozed ? "稍后提醒" : "时间提醒";
        var message = occurrence.Rule.AdvanceMinutes > 0 && !occurrence.IsSnoozed
            ? $"{occurrence.Message}（{occurrence.ScheduledAt:HH:mm}，提前 {occurrence.Rule.AdvanceMinutes} 分钟）"
            : occurrence.Message;

        ShowReminder(message, title);
    }

    public void ShowReminder(string message, string title = "时间提醒")
    {
        var floatingBall = EnsureFloatingBall();
        if (!floatingBall.IsVisible)
        {
            floatingBall.Show();
        }

        var popup = new ReminderPopupWindow(message, title)
        {
            Owner = floatingBall
        };

        popup.PlaceNear(floatingBall.GetReminderAnchor(), floatingBall.GetCurrentWorkArea());
        popup.Show();
    }
}
