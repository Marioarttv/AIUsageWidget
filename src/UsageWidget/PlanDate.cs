using System.Globalization;
using System.Text.Json.Serialization;

namespace UsageWidget;

[JsonConverter(typeof(JsonStringEnumConverter<PlanCycle>))]
public enum PlanCycle
{
    Monthly,
    Yearly,
    // Cancelled: the plan ends on the date and doesn't renew.
    Ends,
}

// A renewal or end date entered by hand: for Claude and Antigravity, whose sign-ins don't report one, or to correct ChatGPT's date.
public sealed record ManualPlanDate(DateOnly Date, PlanCycle Cycle)
{
    public static string Title(PlanCycle cycle) => cycle switch { PlanCycle.Monthly => "Monthly", PlanCycle.Yearly => "Yearly", _ => "Ends" };
    public static string Explanation(PlanCycle cycle) => cycle switch {
        PlanCycle.Monthly => "Renews every month on this day",
        PlanCycle.Yearly => "Renews every year on this day",
        _ => "Cancelled: the plan ends on this day",
    };

    // The renewal on or after today, counted from the entered day so a 31st stays the 31st where the month has one.
    public DateOnly Upcoming(DateOnly today)
    {
        if (Cycle == PlanCycle.Ends || Date >= today) return Date;
        int months = Cycle == PlanCycle.Monthly ? 1 : 12;
        int elapsed = ((today.Year - Date.Year) * 12 + today.Month - Date.Month) / months;
        for (int step = Math.Max(0, elapsed - 1); step <= elapsed + 2; step++)
            if (Date.AddMonths(step * months) is var next && next >= today) return next;
        return Date;
    }

    // "Renews 26 Nov", "Ends 26 Nov", or "Ended 26 Oct".
    public string Label(DateOnly today, IFormatProvider? culture = null)
    {
        var day = Upcoming(today);
        string text = UsageFormatting.Day(day, today, culture);
        if (Cycle != PlanCycle.Ends) return "Renews " + text;
        return (day < today ? "Ended " : "Ends ") + text;
    }

    public bool Lapsed(DateOnly today) => Cycle == PlanCycle.Ends && Date < today;

    // What saving will show, for example "Renews on the 26th of every month · next 26 Dec".
    public string Caption(DateOnly today, IFormatProvider? culture = null)
    {
        var next = Upcoming(today);
        string later = next == Date ? "" : " · next " + UsageFormatting.Day(next, today, culture);
        return Cycle switch {
            PlanCycle.Monthly => $"Renews on the {Ordinal(Date.Day)} of every month" + later,
            PlanCycle.Yearly => "Renews every year on " + Date.ToString("d MMM", culture ?? CultureInfo.CurrentCulture) + later,
            _ => (Date < today ? "Cancelled: ended " : "Cancelled: ends ") + UsageFormatting.Day(Date, today, culture),
        };
    }

    internal static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

// The days of a calendar month laid out in week rows for a date picker.
public static class MonthGrid
{
    // Six week rows of seven days, starting on `firstDay`, so every month takes the same space.
    // null pads the cells before the 1st and after the month's last day.
    public static DateOnly?[] Days(int year, int month, DayOfWeek firstDay)
    {
        var first = new DateOnly(year, month, 1);
        int lead = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        var cells = new DateOnly?[42];
        int count = DateTime.DaysInMonth(year, month);
        for (int i = 0; i < count; i++) cells[lead + i] = first.AddDays(i);
        return cells;
    }

    // Two-letter weekday names in column order, starting on `firstDay`.
    public static string[] WeekdaySymbols(DayOfWeek firstDay, CultureInfo? culture = null)
    {
        var names = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortestDayNames;
        return Enumerable.Range(0, 7).Select(i => names[((int)firstDay + i) % 7]).ToArray();
    }

    // Where the chosen day repeats in the shown month, so a monthly or yearly cycle is visible at a glance.
    public static DateOnly? Repeat(DateOnly day, PlanCycle cycle, int year, int month)
    {
        int months = (year - day.Year) * 12 + month - day.Month;
        if (cycle == PlanCycle.Ends || months <= 0 || (cycle == PlanCycle.Yearly && months % 12 != 0)) return null;
        return day.AddMonths(months);
    }
}
