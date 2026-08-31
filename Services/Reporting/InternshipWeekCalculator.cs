namespace Timenotifier.Services.Reporting;

public sealed record InternshipWorkWeek(int WeekNumber, DateOnly StartDate, DateOnly EndDate)
{
    public string WeekLabel => $"第{WeekNumber}周";
    public string ChineseWeekLabel => $"第{FormatChineseNumber(WeekNumber)}周";
    public string DateRangeLabel => $"{StartDate:M月d日}-{EndDate:M月d日}";

    private static string FormatChineseNumber(int value)
    {
        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        if (value is <= 0 or >= 100)
        {
            return value.ToString();
        }

        if (value < 10)
        {
            return digits[value];
        }

        var tens = value / 10;
        var ones = value % 10;
        var tensText = tens == 1 ? "" : digits[tens];
        return ones == 0
            ? $"{tensText}十"
            : $"{tensText}十{digits[ones]}";
    }
}

public static class InternshipWeekCalculator
{
    public static readonly DateOnly FirstWeekStart = new(2026, 6, 22);
    public static readonly DateOnly FirstWeekEnd = new(2026, 6, 26);

    public static InternshipWorkWeek GetWeek(DateOnly date)
    {
        var monday = GetMonday(date);
        var offsetDays = monday.DayNumber - FirstWeekStart.DayNumber;
        var weekNumber = Math.Max(1, (int)Math.Floor(offsetDays / 7d) + 1);
        var start = FirstWeekStart.AddDays((weekNumber - 1) * 7);
        return new InternshipWorkWeek(weekNumber, start, start.AddDays(4));
    }

    public static InternshipWorkWeek GetWeekForStart(DateOnly startDate)
    {
        return GetWeek(startDate);
    }

    private static DateOnly GetMonday(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }
}
