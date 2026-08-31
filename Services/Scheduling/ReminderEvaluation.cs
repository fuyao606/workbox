using Timenotifier.Models;

namespace Timenotifier.Services.Scheduling;

public static class ReminderEvaluation
{
    public static IReadOnlyList<ReminderOccurrence> GetDueOccurrences(
        DateTime previousCheck,
        DateTime currentCheck,
        ReminderSettings settings,
        ReminderRuntimeState runtimeState)
    {
        if (currentCheck < previousCheck)
        {
            previousCheck = currentCheck;
        }

        runtimeState.Prune(currentCheck.Date);

        List<ReminderOccurrence> dueOccurrences = [];
        foreach (var snoozed in runtimeState.GetDueSnoozes(previousCheck, currentCheck))
        {
            if (runtimeState.IsSuppressedForDate(snoozed.Rule.Id, currentCheck.Date))
            {
                continue;
            }

            dueOccurrences.Add(new ReminderOccurrence(
                snoozed.Rule,
                snoozed.DueAt,
                snoozed.DueAt,
                snoozed.Rule.Message,
                IsSnoozed: true));
        }

        for (var date = previousCheck.Date; date <= currentCheck.Date; date = date.AddDays(1))
        {
            foreach (var reminder in settings.Rules.Where(rule => rule.IsEnabled))
            {
                if (!ShouldRunOnDate(reminder, DateOnly.FromDateTime(date), settings.HolidayAdjustments))
                {
                    continue;
                }

                if (runtimeState.IsSuppressedForDate(reminder.Id, date))
                {
                    continue;
                }

                var scheduledAt = date.Add(reminder.Time.ToTimeSpan());
                var dueAt = scheduledAt.AddMinutes(-Math.Max(0, reminder.AdvanceMinutes));
                if (dueAt <= previousCheck || dueAt > currentCheck)
                {
                    continue;
                }

                var triggerKey = CreateTriggerKey(reminder.Id, dueAt);
                if (!runtimeState.MarkTriggered(triggerKey))
                {
                    continue;
                }

                dueOccurrences.Add(new ReminderOccurrence(
                    reminder,
                    dueAt,
                    scheduledAt,
                    reminder.Message,
                    IsSnoozed: false));
            }
        }

        return dueOccurrences;
    }

    public static IReadOnlyList<ReminderItem> GetDueReminders(
        DateTime previousCheck,
        DateTime currentCheck,
        IReadOnlyList<ReminderItem> reminders,
        ISet<string> triggeredReminders)
    {
        if (currentCheck < previousCheck)
        {
            previousCheck = currentCheck;
        }

        List<ReminderItem> dueReminders = [];
        for (var date = previousCheck.Date; date <= currentCheck.Date; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var reminder in reminders)
            {
                var dueAt = date.Add(reminder.Time.ToTimeSpan());
                if (dueAt <= previousCheck || dueAt > currentCheck)
                {
                    continue;
                }

                var triggerKey = $"{dueAt:yyyy-MM-dd HH:mm:ss}:{reminder.Message}";
                if (!triggeredReminders.Add(triggerKey))
                {
                    continue;
                }

                dueReminders.Add(reminder);
            }
        }

        return dueReminders;
    }

    private static bool ShouldRunOnDate(
        ReminderRule reminder,
        DateOnly date,
        IReadOnlyCollection<HolidayAdjustment> holidayAdjustments)
    {
        return reminder.ScheduleKind switch
        {
            ReminderScheduleKind.Daily => true,
            ReminderScheduleKind.SpecificWeekdays => reminder.Weekdays.Contains(date.DayOfWeek),
            ReminderScheduleKind.Once => reminder.OnceDate == date,
            _ => IsWorkday(date, holidayAdjustments)
        };
    }

    private static bool IsWorkday(DateOnly date, IReadOnlyCollection<HolidayAdjustment> holidayAdjustments)
    {
        var adjustment = holidayAdjustments.LastOrDefault(item => item.Date == date);
        if (adjustment is not null)
        {
            return adjustment.Kind == HolidayAdjustmentKind.MakeUpWorkday;
        }

        return date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
    }

    private static string CreateTriggerKey(string reminderId, DateTime dueAt)
    {
        return $"{dueAt:yyyy-MM-dd HH:mm:ss}:{reminderId}";
    }
}

public sealed class ReminderRuntimeState
{
    private readonly HashSet<string> _triggeredReminders = [];
    private readonly Dictionary<string, SnoozedReminder> _snoozedReminders = [];
    private readonly Dictionary<string, DateTime> _suppressedUntilDate = [];

    public bool MarkTriggered(string triggerKey)
    {
        return _triggeredReminders.Add(triggerKey);
    }

    public void Snooze(ReminderRule rule, DateTime dueAt)
    {
        _snoozedReminders[rule.Id] = new SnoozedReminder(rule.Id, dueAt) { Rule = rule };
    }

    public void SuppressToday(ReminderRule rule, DateTime now)
    {
        _suppressedUntilDate[rule.Id] = now.Date;
    }

    public bool IsSuppressedForDate(string ruleId, DateTime date)
    {
        return _suppressedUntilDate.TryGetValue(ruleId, out var suppressedDate)
            && suppressedDate.Date == date.Date;
    }

    public IReadOnlyList<SnoozedReminder> GetDueSnoozes(DateTime previousCheck, DateTime currentCheck)
    {
        List<SnoozedReminder> due = [];
        foreach (var (ruleId, reminder) in _snoozedReminders.ToArray())
        {
            var dueAt = reminder.DueAt;
            if (dueAt <= previousCheck || dueAt > currentCheck)
            {
                continue;
            }

            _snoozedReminders.Remove(ruleId);
            due.Add(reminder);
        }

        return due;
    }

    public void AttachRules(IReadOnlyCollection<ReminderRule> rules)
    {
        var ruleMap = rules.ToDictionary(rule => rule.Id);
        foreach (var (ruleId, snoozed) in _snoozedReminders.ToArray())
        {
            if (!ruleMap.TryGetValue(ruleId, out var rule))
            {
                _snoozedReminders.Remove(ruleId);
                continue;
            }

            snoozed.Rule = rule;
        }
    }

    public void Prune(DateTime today)
    {
        foreach (var (ruleId, suppressedDate) in _suppressedUntilDate.ToArray())
        {
            if (suppressedDate.Date < today.Date)
            {
                _suppressedUntilDate.Remove(ruleId);
            }
        }
    }
}

public sealed record SnoozedReminder(string RuleId, DateTime DueAt)
{
    public ReminderRule Rule { get; set; } = new();
}
