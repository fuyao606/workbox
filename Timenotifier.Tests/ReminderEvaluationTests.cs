using System.Reflection;
using Timenotifier.Models;
using Xunit;
using Timenotifier.Services.Infrastructure;
using Timenotifier.Services.Scheduling;
using Timenotifier.Services.Settings;

namespace Timenotifier.Tests;

public sealed class ReminderEvaluationTests
{
    [Theory]
    [InlineData(9, 0, "上班打卡")]
    [InlineData(12, 0, "午休")]
    [InlineData(14, 0, "上班")]
    [InlineData(18, 0, "下班打卡、日报填写")]
    public void Returns_due_workday_reminder_at_expected_time(int hour, int minute, string expectedMessage)
    {
        var triggeredReminders = new HashSet<string>();
        var dueAt = new DateTime(2026, 6, 25, hour, minute, 0);

        var dueReminders = ReminderEvaluation.GetDueReminders(
            dueAt.AddSeconds(-1),
            dueAt,
            DefaultReminders.WorkdayReminders,
            triggeredReminders);

        Assert.Single(dueReminders);
        Assert.Equal(expectedMessage, dueReminders[0].Message);
    }

    [Theory]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Does_not_trigger_on_weekend(DayOfWeek dayOfWeek)
    {
        var triggeredReminders = new HashSet<string>();
        var dueAt = new DateTime(2026, 6, 27 + ((int)dayOfWeek - (int)DayOfWeek.Saturday), 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueReminders(
            dueAt.AddSeconds(-1),
            dueAt,
            DefaultReminders.WorkdayReminders,
            triggeredReminders);

        Assert.Empty(dueReminders);
    }

    [Fact]
    public void Suppresses_duplicate_reminder_within_same_minute()
    {
        var triggeredReminders = new HashSet<string>();
        var dueAt = new DateTime(2026, 6, 25, 9, 0, 0);

        var firstPass = ReminderEvaluation.GetDueReminders(
            dueAt.AddSeconds(-1),
            dueAt,
            DefaultReminders.WorkdayReminders,
            triggeredReminders);
        var secondPass = ReminderEvaluation.GetDueReminders(
            dueAt.AddSeconds(-1),
            dueAt,
            DefaultReminders.WorkdayReminders,
            triggeredReminders);

        Assert.Single(firstPass);
        Assert.Empty(secondPass);
    }

    [Fact]
    public void Scheduler_catches_reminder_when_timer_runs_after_due_minute()
    {
        var provider = new FakeTimeProvider(new DateTime(2026, 6, 25, 17, 59, 59));
        List<string> messages = [];
        var scheduler = new ReminderScheduler(provider, reminder => messages.Add(reminder.Message));

        InvokeSchedulerCheck(scheduler);
        provider.Now = new DateTime(2026, 6, 25, 18, 0, 1);
        InvokeSchedulerCheck(scheduler);

        Assert.Contains("下班打卡、日报填写", messages);
    }

    [Fact]
    public async Task Scheduler_reload_does_not_backfill_newly_added_past_rule()
    {
        var provider = new FakeTimeProvider(new DateTime(2026, 6, 25, 9, 0, 10));
        var store = new MutableReminderSettingsStore(new ReminderSettings());
        List<ReminderOccurrence> reminders = [];
        var scheduler = new ReminderScheduler(provider, store, reminders.Add);

        await scheduler.StartAsync();
        provider.Now = new DateTime(2026, 6, 25, 9, 0, 20);
        store.Settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        store.Settings.Rules[0].Time = new TimeOnly(9, 0, 15);
        await scheduler.ReloadSettingsAsync();
        InvokeSchedulerCheck(scheduler);

        Assert.Empty(reminders);
    }

    [Fact]
    public void Default_settings_preserve_legacy_workday_reminders()
    {
        var runtimeState = new ReminderRuntimeState();
        var dueAt = new DateTime(2026, 6, 25, 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            dueAt.AddSeconds(-1),
            dueAt,
            DefaultReminders.CreateSettings(),
            runtimeState);

        Assert.Single(dueReminders);
        Assert.Equal("上班打卡", dueReminders[0].Message);
    }

    [Fact]
    public void Workday_reminder_runs_on_makeup_weekend()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        settings.HolidayAdjustments.Add(new HolidayAdjustment
        {
            Date = new DateOnly(2026, 6, 27),
            Kind = HolidayAdjustmentKind.MakeUpWorkday,
            Name = "调休"
        });
        var dueAt = new DateTime(2026, 6, 27, 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            dueAt.AddSeconds(-1),
            dueAt,
            settings,
            runtimeState);

        Assert.Single(dueReminders);
    }

    [Fact]
    public void Workday_reminder_skips_manual_holiday()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        settings.HolidayAdjustments.Add(new HolidayAdjustment
        {
            Date = new DateOnly(2026, 6, 25),
            Kind = HolidayAdjustmentKind.Holiday,
            Name = "休假"
        });
        var dueAt = new DateTime(2026, 6, 25, 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            dueAt.AddSeconds(-1),
            dueAt,
            settings,
            runtimeState);

        Assert.Empty(dueReminders);
    }

    [Theory]
    [InlineData(2026, 6, 27)]
    [InlineData(2026, 6, 29)]
    public void Daily_reminder_runs_on_weekends_and_workdays(int year, int month, int day)
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Daily);
        var dueAt = new DateTime(year, month, day, 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            dueAt.AddSeconds(-1),
            dueAt,
            settings,
            runtimeState);

        Assert.Single(dueReminders);
    }

    [Fact]
    public void Specific_weekday_reminder_only_runs_on_selected_days()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.SpecificWeekdays);
        settings.Rules[0].Weekdays = [DayOfWeek.Wednesday];
        var selectedDay = new DateTime(2026, 6, 24, 9, 0, 0);
        var otherDay = new DateTime(2026, 6, 25, 9, 0, 0);

        var selectedResult = ReminderEvaluation.GetDueOccurrences(
            selectedDay.AddSeconds(-1),
            selectedDay,
            settings,
            runtimeState);
        var otherResult = ReminderEvaluation.GetDueOccurrences(
            otherDay.AddSeconds(-1),
            otherDay,
            settings,
            runtimeState);

        Assert.Single(selectedResult);
        Assert.Empty(otherResult);
    }

    [Fact]
    public void Once_reminder_only_runs_on_selected_date()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Once);
        settings.Rules[0].OnceDate = new DateOnly(2026, 6, 25);
        var selectedDay = new DateTime(2026, 6, 25, 9, 0, 0);
        var otherDay = new DateTime(2026, 6, 26, 9, 0, 0);

        var selectedResult = ReminderEvaluation.GetDueOccurrences(
            selectedDay.AddSeconds(-1),
            selectedDay,
            settings,
            runtimeState);
        var otherResult = ReminderEvaluation.GetDueOccurrences(
            otherDay.AddSeconds(-1),
            otherDay,
            settings,
            runtimeState);

        Assert.Single(selectedResult);
        Assert.Empty(otherResult);
    }

    [Fact]
    public void Advance_minutes_trigger_before_scheduled_time()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        settings.Rules[0].Time = new TimeOnly(18, 0);
        settings.Rules[0].AdvanceMinutes = 10;
        var dueAt = new DateTime(2026, 6, 25, 17, 50, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            dueAt.AddSeconds(-1),
            dueAt,
            settings,
            runtimeState);

        Assert.Single(dueReminders);
        Assert.Equal(new DateTime(2026, 6, 25, 18, 0, 0), dueReminders[0].ScheduledAt);
    }

    [Fact]
    public void Excessive_advance_minutes_do_not_trigger_on_previous_day()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        settings.Rules[0].Time = new TimeOnly(9, 0);
        settings.Rules[0].AdvanceMinutes = 24 * 60;
        var previousDay = new DateTime(2026, 6, 24, 9, 0, 0);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            previousDay.AddSeconds(-1),
            previousDay,
            settings,
            runtimeState);

        Assert.Empty(dueReminders);
    }

    [Fact]
    public void Snoozed_reminder_runs_at_snooze_time()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        var rule = settings.Rules[0];
        var snoozeAt = new DateTime(2026, 6, 25, 9, 10, 0);
        runtimeState.Snooze(rule, snoozeAt);

        var dueReminders = ReminderEvaluation.GetDueOccurrences(
            snoozeAt.AddSeconds(-1),
            snoozeAt,
            settings,
            runtimeState);

        Assert.Single(dueReminders);
        Assert.True(dueReminders[0].IsSnoozed);
    }

    [Fact]
    public void Suppress_today_only_suppresses_current_date()
    {
        var runtimeState = new ReminderRuntimeState();
        var settings = SingleRuleSettings(ReminderScheduleKind.Workdays);
        var rule = settings.Rules[0];
        runtimeState.SuppressToday(rule, new DateTime(2026, 6, 25, 8, 0, 0));
        var suppressedDueAt = new DateTime(2026, 6, 25, 9, 0, 0);
        var nextDayDueAt = new DateTime(2026, 6, 26, 9, 0, 0);

        var suppressedResult = ReminderEvaluation.GetDueOccurrences(
            suppressedDueAt.AddSeconds(-1),
            suppressedDueAt,
            settings,
            runtimeState);
        var nextDayResult = ReminderEvaluation.GetDueOccurrences(
            nextDayDueAt.AddSeconds(-1),
            nextDayDueAt,
            settings,
            runtimeState);

        Assert.Empty(suppressedResult);
        Assert.Single(nextDayResult);
    }

    [Theory]
    [InlineData(9, 0, 0, "上班打卡")]
    [InlineData(12, 0, 0, "午休")]
    [InlineData(14, 0, 0, "上班")]
    [InlineData(18, 0, 0, "下班打卡、日报填写")]
    public void Triggers_when_check_window_crosses_exact_second(
        int hour,
        int minute,
        int second,
        string expectedMessage)
    {
        var triggeredReminders = new HashSet<string>();
        var dueAt = new DateTime(2026, 6, 26, hour, minute, second);

        var dueReminders = ReminderEvaluation.GetDueReminders(
            dueAt.AddMilliseconds(-500),
            dueAt.AddMilliseconds(500),
            DefaultReminders.WorkdayReminders,
            triggeredReminders);

        Assert.Single(dueReminders);
        Assert.Equal(expectedMessage, dueReminders[0].Message);
    }

    private static void InvokeSchedulerCheck(ReminderScheduler scheduler)
    {
        typeof(ReminderScheduler)
            .GetMethod("CheckReminders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, null);
    }

    private static ReminderSettings SingleRuleSettings(ReminderScheduleKind scheduleKind)
    {
        return new ReminderSettings
        {
            Rules =
            [
                new ReminderRule
                {
                    Id = "test-rule",
                    IsEnabled = true,
                    Message = "测试提醒",
                    Time = new TimeOnly(9, 0),
                    ScheduleKind = scheduleKind
                }
            ],
            SnoozeMinutes = 10
        };
    }

    private sealed class FakeTimeProvider(DateTime now) : ITimeProvider
    {
        public DateTime Now { get; set; } = now;
    }

    private sealed class MutableReminderSettingsStore(ReminderSettings settings) : IAppSettingsStore
    {
        public ReminderSettings Settings { get; set; } = settings;

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
            return Task.FromResult(Settings);
        }

        public Task SaveReminderSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReportTemplate>>([]);
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
