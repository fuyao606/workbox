using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Scheduling;

public sealed class DailyReportAutomationScheduler(
    IAppSettingsStore settingsStore,
    ITimeProvider timeProvider,
    Func<Task> runAutomationAsync)
{
    private static readonly TimeSpan TriggerWindow = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromMinutes(1)
    };
    private DailyReportAutomationSettings _settings = new();
    private DateOnly? _lastTriggeredDate;
    private bool _isRunning;

    public async Task StartAsync()
    {
        await ReloadAsync();
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public async Task ReloadAsync()
    {
        _settings = await settingsStore.LoadDailyReportAutomationSettingsAsync();
    }

    private async Task CheckAsync()
    {
        if (_isRunning || !_settings.IsEnabled)
        {
            return;
        }

        var now = timeProvider.Now;
        var today = DateOnly.FromDateTime(now);
        if (_lastTriggeredDate == today)
        {
            return;
        }

        var dueAt = today.ToDateTime(_settings.RunTime);
        if (now < dueAt || now >= dueAt.Add(TriggerWindow))
        {
            return;
        }

        _lastTriggeredDate = today;
        _isRunning = true;
        try
        {
            await runAutomationAsync();
        }
        finally
        {
            _isRunning = false;
        }
    }
}

