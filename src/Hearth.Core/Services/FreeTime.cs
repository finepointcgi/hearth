using System.Text;

namespace Hearth.Core.Services;

/// <summary>
/// Finds open slots between busy times, for "when am I free?" and "find 30 minutes with Anna this week". Works on
/// plain busy ranges so Google and Outlook calendars share it. Only working hours count, and weekends are skipped
/// unless asked for.
/// </summary>
public static class FreeTime
{
    public sealed record Slot(DateTimeOffset Start, DateTimeOffset End)
    {
        public TimeSpan Length => End - Start;
    }

    /// <summary>
    /// Open slots at least <paramref name="minutes"/> long between <paramref name="from"/> and <paramref name="to"/>,
    /// inside <paramref name="dayStart"/>–<paramref name="dayEnd"/> each day (local to <paramref name="from"/>'s offset).
    /// Starts are rounded up to the next quarter hour.
    /// </summary>
    public static List<Slot> Find(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> busy, DateTimeOffset from, DateTimeOffset to,
        int minutes, TimeOnly dayStart, TimeOnly dayEnd, bool weekends = false, int max = 12)
    {
        var need = TimeSpan.FromMinutes(Math.Max(5, minutes));
        var taken = busy.Where(b => b.End > b.Start).Select(b => (b.Start.ToOffset(from.Offset), b.End.ToOffset(from.Offset)))
            .OrderBy(b => b.Item1).ToList();
        var slots = new List<Slot>();
        for (var day = from.Date; day < to.Date.AddDays(1) && slots.Count < max; day = day.AddDays(1))
        {
            if (!weekends && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var open = new DateTimeOffset(day + dayStart.ToTimeSpan(), from.Offset);
            var close = new DateTimeOffset(day + dayEnd.ToTimeSpan(), from.Offset);
            if (open < from) open = from;
            if (close > to) close = to;
            var cursor = Quarter(open);
            foreach (var (start, end) in taken.Where(b => b.Item2 > cursor && b.Item1 < close))
            {
                if (start - cursor >= need) slots.Add(new Slot(cursor, start));
                if (end > cursor) cursor = Quarter(end);
            }
            if (close - cursor >= need) slots.Add(new Slot(cursor, close));
        }
        return slots.Take(max).ToList();
    }

    /// <summary>The slots as text for the model, or a plain "nothing free" line.</summary>
    public static string Describe(List<Slot> slots, int minutes, DateTimeOffset from, DateTimeOffset to)
    {
        if (slots.Count == 0) return $"No free {minutes}-minute slot between {from:ddd MMM d} and {to:ddd MMM d} in working hours.";
        var sb = new StringBuilder($"Free for at least {minutes} min (times are {TimeZoneInfo.Local.Id}):\n");
        foreach (var s in slots)
            sb.AppendLine($"- {s.Start:ddd MMM d}: {s.Start:h:mm tt}–{s.End:h:mm tt} ({Hours(s.Length)})");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Reads "09:00"-style times, falling back to <paramref name="fallback"/>.</summary>
    public static TimeOnly Time(string? text, TimeOnly fallback) => TimeOnly.TryParse(text, out var t) ? t : fallback;

    private static string Hours(TimeSpan t) => t.TotalMinutes < 60 ? $"{t.TotalMinutes:0} min"
        : t.Minutes == 0 ? $"{t.TotalHours:0} h" : $"{(int)t.TotalHours} h {t.Minutes} min";

    private static DateTimeOffset Quarter(DateTimeOffset t)
    {
        var extra = t.Minute % 15 == 0 && t.Second == 0 && t.Millisecond == 0 ? 0 : 15 - t.Minute % 15;
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset).AddMinutes(extra);
    }
}
