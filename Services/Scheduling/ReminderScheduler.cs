using System.Windows.Threading;
using Timenotifier.Models;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Settings;

namespace Timenotifier.Services.Scheduling;

public sealed class ReminderScheduler
{
    private static readonly TimeSpan MaxCatchUpWindow = TimeSpan.FromMinutes(5);

    private readonly ITimeProvider _timeProvider;
    private readonly IAppSettingsStore _settingsStore;
    private readonly Action<ReminderOccurrence> _onReminderDue;
    private readonly DispatcherTimer _timer;
    private readonly ReminderRuntimeState _runtimeState = new();
    private ReminderSettings _settings = DefaultReminders.CreateSettings();
    private DateTime? _lastCheckedAt;

    public ReminderScheduler(
        ITimeProvider timeProvider,
        IAppSettingsStore settingsStore,
        Action<ReminderOccurrence> onReminderDue)
    {
        _timeProvider = timeProvider;
        _settingsStore = settingsStore;
        _onReminderDue = onReminderDue;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => CheckReminders();
    }

    public ReminderScheduler(ITimeProvider timeProvider, Action<ReminderItem> onReminderDue)
        : this(
            timeProvider,
            new InMemoryReminderSettingsStore(DefaultReminders.CreateSettings()),
            occurrence => onReminderDue(new ReminderItem(occurrence.Rule.Time, occurrence.Message)))
    {
    }

    public async Task StartAsync()
    {
        await ReloadSettingsAsync();
        _lastCheckedAt = _timeProvider.Now;
        _timer.Start();
    }

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public async Task ReloadSettingsAsync()
    {
        _settings = await _settingsStore.LoadReminderSettingsAsync();
        _runtimeState.AttachRules(_settings.Rules);
        _lastCheckedAt = _timeProvider.Now;
    }

    public void Snooze(ReminderOccurrence occurrence)
    {
        var minutes = Math.Clamp(_settings.SnoozeMinutes <= 0 ? 10 : _settings.SnoozeMinutes, 1, 240);
        _runtimeState.Snooze(occurrence.Rule, _timeProvider.Now.AddMinutes(minutes));
    }

    public void SuppressToday(ReminderOccurrence occurrence)
    {
        _runtimeState.SuppressToday(occurrence.Rule, _timeProvider.Now);
    }

    private void CheckReminders()
    {
        var now = _timeProvider.Now;
        var previousCheck = _lastCheckedAt ?? now;

        if (now - previousCheck > MaxCatchUpWindow)
        {
            previousCheck = now - MaxCatchUpWindow;
        }

        foreach (var reminder in ReminderEvaluation.GetDueOccurrences(
                     previousCheck,
                     now,
                     _settings,
                     _runtimeState))
        {
            _onReminderDue(reminder);
        }

        _lastCheckedAt = now;
    }

    private sealed class InMemoryReminderSettingsStore(ReminderSettings settings) : IAppSettingsStore
    {
        private ReminderSettings _settings = settings;

        public Task<ModelProviderSettings> LoadModelSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderSettings());
        }

        public Task SaveModelSettingsAsync(ModelProviderSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<ReminderSettings> LoadReminderSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReportTemplate>>(AppSettingsStore.CreateDefaultReportTemplates());
        }

        public Task SaveReportTemplatesAsync(IReadOnlyList<ReportTemplate> templates, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<EvidenceCaptureSettings> LoadEvidenceCaptureSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvidenceCaptureSettings());
        }

        public Task SaveEvidenceCaptureSettingsAsync(EvidenceCaptureSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<DailyReportAutomationSettings> LoadDailyReportAutomationSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DailyReportAutomationSettings());
        }

        public Task SaveDailyReportAutomationSettingsAsync(DailyReportAutomationSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
