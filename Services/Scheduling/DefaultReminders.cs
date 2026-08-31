using Timenotifier.Models;

namespace Timenotifier.Services.Scheduling;

public static class DefaultReminders
{
    public static IReadOnlyList<ReminderItem> WorkdayReminders { get; } =
    [
        new(new TimeOnly(9, 0), "上班打卡"),
        new(new TimeOnly(12, 0), "午休"),
        new(new TimeOnly(14, 0), "上班"),
        new(new TimeOnly(18, 0), "下班打卡、日报填写")
    ];

    public static ReminderSettings CreateSettings()
    {
        return new ReminderSettings
        {
            Rules =
            [
                CreateWorkdayRule("上班打卡", new TimeOnly(9, 0)),
                CreateWorkdayRule("午休", new TimeOnly(12, 0)),
                CreateWorkdayRule("上班", new TimeOnly(14, 0)),
                CreateWorkdayRule("下班打卡、日报填写", new TimeOnly(18, 0))
            ],
            SnoozeMinutes = 10
        };
    }

    private static ReminderRule CreateWorkdayRule(string message, TimeOnly time)
    {
        return new ReminderRule
        {
            Id = Guid.NewGuid().ToString("N"),
            IsEnabled = true,
            Message = message,
            Time = time,
            ScheduleKind = ReminderScheduleKind.Workdays,
            AdvanceMinutes = 0
        };
    }
}
