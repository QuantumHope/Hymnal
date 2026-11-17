namespace Hymnal.Core.Services.Sprints;

public enum StreakChange
{
    /// <summary>The first day ever with a sprint.</summary>
    Started,

    /// <summary>Yesterday had a sprint and today is the first of today: the streak grows by one.</summary>
    Extended,

    /// <summary>Today already had a sprint: the streak is unchanged.</summary>
    Kept,

    /// <summary>An earlier streak lapsed; this sprint starts a new one.</summary>
    Restarted,
}

/// <param name="Change">What this sprint did to the streak.</param>
/// <param name="Current">The streak in days after this sprint.</param>
/// <param name="Previous">For <see cref="StreakChange.Restarted"/>, the length of the streak that lapsed; otherwise 0.</param>
/// <param name="Best">The longest streak ever, counting this sprint.</param>
public sealed record StreakOutcome(StreakChange Change, int Current, int Previous, int Best)
{
    public string Describe() => Change switch
    {
        StreakChange.Started => "Streak started — day 1",
        StreakChange.Extended => $"Streak extended to {Days(Current)}",
        StreakChange.Kept => $"Streak kept alive — {Days(Current)}",
        StreakChange.Restarted => $"New streak started — your last one was {Days(Previous)}",
        _ => "",
    };

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";
}

/// <summary>Daily streaks over the local dates that had at least one sprint.</summary>
public static class StreakCalculator
{
    /// <summary>What a sprint on <paramref name="today"/> does, given the dates that already had sprints.</summary>
    public static StreakOutcome Apply(IEnumerable<DateOnly> earlierDates, DateOnly today)
    {
        var days = new HashSet<DateOnly>(earlierDates);
        var yesterday = today.AddDays(-1);

        StreakOutcome outcome;
        if (days.Contains(today))
            outcome = new StreakOutcome(StreakChange.Kept, RunEndingAt(days, today), 0, 0);
        else if (days.Contains(yesterday))
            outcome = new StreakOutcome(StreakChange.Extended, RunEndingAt(days, yesterday) + 1, 0, 0);
        else if (days.Count > 0)
            outcome = new StreakOutcome(StreakChange.Restarted, 1, RunEndingAt(days, days.Max()), 0);
        else
            outcome = new StreakOutcome(StreakChange.Started, 1, 0, 0);

        days.Add(today);
        return outcome with { Best = Best(days) };
    }

    /// <summary>The current streak: consecutive days ending today, or yesterday if today has no sprint yet.</summary>
    public static int Current(IEnumerable<DateOnly> dates, DateOnly today)
    {
        var days = new HashSet<DateOnly>(dates);
        if (days.Contains(today))
            return RunEndingAt(days, today);
        var yesterday = today.AddDays(-1);
        return days.Contains(yesterday) ? RunEndingAt(days, yesterday) : 0;
    }

    public static int Best(IEnumerable<DateOnly> dates)
    {
        var days = dates as HashSet<DateOnly> ?? new HashSet<DateOnly>(dates);
        var best = 0;
        foreach (var d in days)
        {
            if (days.Contains(d.AddDays(-1)))
                continue; // not the start of a run
            var len = 1;
            while (days.Contains(d.AddDays(len)))
                len++;
            best = Math.Max(best, len);
        }
        return best;
    }

    private static int RunEndingAt(HashSet<DateOnly> days, DateOnly end)
    {
        var len = 0;
        while (days.Contains(end.AddDays(-len)))
            len++;
        return len;
    }
}
