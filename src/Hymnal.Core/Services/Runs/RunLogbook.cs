using Hymnal.Core.Models.Runs;

namespace Hymnal.Core.Services.Runs;

/// <summary>One day in the Logbook heatmap.</summary>
/// <param name="Level">0 for no run; 1–4 by minutes written that day (under 15, under 30, under 60, 60 or more).</param>
public sealed record LogbookDay(DateOnly Date, int Runs, int Minutes, int NetWords, int Level, bool IsFuture);

/// <summary>Lifetime totals over every recorded run.</summary>
public sealed record LogbookTotals(int Runs, int Minutes, int WordsAdded, int WordsCut, int WordsNet, int Xp,
    int Completed, int DaysWritten);

/// <summary>The Logbook's numbers: streaks, totals and the heatmap, from the writer profile's runs.</summary>
public sealed record LogbookSummary(
    int CurrentStreak,
    int BestStreak,
    LogbookTotals Totals,
    IReadOnlyList<LogbookDay> Heatmap,
    IReadOnlyList<RunRecord> Runs);

public static class RunLogbook
{
    /// <summary>Weeks in the heatmap.</summary>
    public const int Weeks = 12;

    /// <summary>
    /// The Logbook for <paramref name="runs"/> as of <paramref name="today"/>. The heatmap runs Monday to Sunday in
    /// columns, ending with the week that holds today (later days in that week are marked future). Runs are listed
    /// newest first.
    /// </summary>
    public static LogbookSummary Build(IEnumerable<RunRecord> runs, DateOnly today)
    {
        var list = runs.ToList();
        var dated = list
            .Select(r => (Run: r, Ok: DateOnly.TryParse(r.LocalDate, out var d), Date: d))
            .Where(x => x.Ok)
            .ToList();
        var dates = dated.Select(x => x.Date).ToHashSet();

        var totals = new LogbookTotals(
            Runs: list.Count,
            Minutes: list.Sum(r => r.ActualSeconds) / 60,
            WordsAdded: list.Sum(r => r.WordsAdded),
            WordsCut: list.Sum(r => r.WordsCut),
            WordsNet: list.Sum(r => r.WordsNet),
            Xp: list.Sum(r => RunScoring.Xp(r.WordsAdded, r.WordsCut, r.ActualSeconds, r.Completed)),
            Completed: list.Count(r => r.Completed),
            DaysWritten: dates.Count);

        var byDay = dated
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => (Runs: g.Count(), Seconds: g.Sum(x => x.Run.ActualSeconds),
                Net: g.Sum(x => x.Run.WordsNet)));

        // Columns are weeks starting Monday; the last column holds today.
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        var start = today.AddDays(-mondayOffset - 7 * (Weeks - 1));
        var heatmap = new List<LogbookDay>(Weeks * 7);
        for (var i = 0; i < Weeks * 7; i++)
        {
            var date = start.AddDays(i);
            byDay.TryGetValue(date, out var day);
            var minutes = day.Seconds / 60;
            var level = day.Runs == 0 ? 0 : minutes switch { < 15 => 1, < 30 => 2, < 60 => 3, _ => 4 };
            heatmap.Add(new LogbookDay(date, day.Runs, minutes, day.Net, level, date > today));
        }

        return new LogbookSummary(
            StreakCalculator.Current(dates, today),
            StreakCalculator.Best(dates),
            totals,
            heatmap,
            list.OrderByDescending(r => r.StartedAt).ToList());
    }
}
