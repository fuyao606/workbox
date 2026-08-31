namespace Timenotifier.Models;

public sealed record ReminderItem(TimeOnly Time, string Message)
{
    public string DisplayTime => Time.ToString("HH:mm");
}

public enum ReminderScheduleKind
{
    Workdays,
    Daily,
    SpecificWeekdays,
    Once
}

public enum HolidayAdjustmentKind
{
    Holiday,
    MakeUpWorkday
}

public sealed class ReminderRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsEnabled { get; set; } = true;
    public string Message { get; set; } = "";
    public TimeOnly Time { get; set; }
    public ReminderScheduleKind ScheduleKind { get; set; } = ReminderScheduleKind.Workdays;
    public HashSet<DayOfWeek> Weekdays { get; set; } = [];
    public DateOnly? OnceDate { get; set; }
    public int AdvanceMinutes { get; set; }

    public string DisplayTime => Time.ToString("HH:mm");
}

public sealed class HolidayAdjustment
{
    public DateOnly Date { get; set; }
    public HolidayAdjustmentKind Kind { get; set; } = HolidayAdjustmentKind.Holiday;
    public string Name { get; set; } = "";
}

public sealed class ReminderSettings
{
    public List<ReminderRule> Rules { get; set; } = [];
    public List<HolidayAdjustment> HolidayAdjustments { get; set; } = [];
    public int SnoozeMinutes { get; set; } = 10;
}

public sealed record ReminderOccurrence(
    ReminderRule Rule,
    DateTime DueAt,
    DateTime ScheduledAt,
    string Message,
    bool IsSnoozed);
