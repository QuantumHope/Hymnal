using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models.Runs;
using Hymnal.Core.Services.Runs;

namespace Hymnal.ViewModels;

/// <summary>One square of the Logbook heatmap.</summary>
public sealed record LogbookCell(int Level, bool IsFuture, bool IsToday, string Tip)
{
    public bool IsL1 => Level == 1;
    public bool IsL2 => Level == 2;
    public bool IsL3 => Level == 3;
    public bool IsL4 => Level == 4;
}

/// <summary>One run in the Logbook list, already formatted.</summary>
public sealed record LogbookRow(
    string Date,
    string Time,
    string Project,
    string Duration,
    string Net,
    string Detail,
    string Xp,
    bool Completed,
    string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>
/// The Logbook: every run in the global writer profile, with the streak, lifetime totals, a 12-week heatmap and the
/// note left after each run. Built once when it opens; read-only.
/// </summary>
public sealed class RunLogbookViewModel
{
    private RunLogbookViewModel(LogbookSummary? summary, string? error, IReadOnlyDictionary<string, string> notes,
        DateOnly today)
    {
        Error = error;
        if (summary == null)
        {
            Cells = [];
            Rows = [];
            return;
        }

        CurrentStreak = summary.CurrentStreak.ToString(CultureInfo.CurrentCulture);
        CurrentStreakLabel = summary.CurrentStreak == 1 ? "DAY STREAK" : "DAYS STREAK";
        BestStreak = $"Best: {Days(summary.BestStreak)}";
        var t = summary.Totals;
        TotalRuns = t.Runs.ToString("N0");
        TotalTime = t.Minutes >= 60 ? $"{t.Minutes / 60} h {t.Minutes % 60} min" : $"{t.Minutes} min";
        TotalWords = Signed(t.WordsNet);
        TotalWordsDetail = $"+{t.WordsAdded:N0} added · −{t.WordsCut:N0} cut";
        TotalXp = $"{t.Xp:N0} XP";
        TotalDetail = $"{t.Completed:N0} of {t.Runs:N0} runs went the distance · {Days(t.DaysWritten)} written";

        Cells = summary.Heatmap
            .Select(d => new LogbookCell(d.Level, d.IsFuture, d.Date == today, d.IsFuture
                ? ""
                : d.Runs == 0
                    ? $"{d.Date:ddd MMM d}: no runs"
                    : $"{d.Date:ddd MMM d}: {d.Runs} {(d.Runs == 1 ? "run" : "runs")}, {d.Minutes} min, {Signed(d.NetWords)} words"))
            .ToList();
        var first = summary.Heatmap.Count > 0 ? summary.Heatmap[0].Date : today;
        HeatmapRange = $"{first:MMM d} – {today:MMM d}";

        Rows = summary.Runs.Select(r =>
        {
            var local = r.StartedAt.ToLocalTime();
            var xp = RunScoring.Xp(r.WordsAdded, r.WordsCut, r.ActualSeconds, r.Completed);
            var minutes = r.ActualSeconds / 60;
            var seconds = r.ActualSeconds % 60;
            return new LogbookRow(
                Date: local.ToString("ddd MMM d", CultureInfo.CurrentCulture),
                Time: local.ToString("t", CultureInfo.CurrentCulture),
                Project: string.IsNullOrEmpty(r.Workspace) ? "—" : r.Workspace,
                Duration: minutes == 0 ? $"{seconds} s" : $"{minutes} min",
                Net: Signed(r.WordsNet),
                Detail: $"+{r.WordsAdded:N0} / −{r.WordsCut:N0}",
                Xp: $"+{xp:N0} XP",
                Completed: r.Completed,
                Note: notes.TryGetValue(r.Id, out var note) ? note : null);
        }).ToList();
    }

    /// <summary>Why the Logbook is empty when the profile can't be read; null otherwise.</summary>
    public string? Error { get; }
    public bool HasError => Error != null;
    public bool IsEmpty => Error == null && Rows.Count == 0;
    public bool HasRuns => Rows.Count > 0;

    public string CurrentStreak { get; } = "0";
    public string CurrentStreakLabel { get; } = "DAYS STREAK";
    public string BestStreak { get; } = "";
    public string TotalRuns { get; } = "0";
    public string TotalTime { get; } = "";
    public string TotalWords { get; } = "";
    public string TotalWordsDetail { get; } = "";
    public string TotalXp { get; } = "";
    public string TotalDetail { get; } = "";
    public string HeatmapRange { get; } = "";

    /// <summary>Heatmap days, Monday-first weeks in order (fill a 7-tall column layout).</summary>
    public IReadOnlyList<LogbookCell> Cells { get; }

    /// <summary>Runs, newest first.</summary>
    public IReadOnlyList<LogbookRow> Rows { get; }

    /// <summary>
    /// Loads the profile and the notes left after each run (from each workspace a run was in; a workspace that
    /// is gone or unreadable just has no notes).
    /// </summary>
    public static async Task<RunLogbookViewModel> LoadAsync(IWriterProfileStore profile, IFutureYouNotesService notes,
        DateOnly today)
    {
        var loaded = await profile.LoadRunsAsync().ConfigureAwait(false);
        if (!loaded.IsSuccess)
            return new RunLogbookViewModel(null, $"The Logbook can't be shown: {loaded.Error}",
                new Dictionary<string, string>(), today);

        var runs = loaded.Value!;
        var byRun = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in runs.Select(r => r.WorkspacePath).Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            var result = await notes.LoadAsync(path!).ConfigureAwait(false);
            if (!result.IsSuccess)
                continue;
            foreach (var note in result.Value!)
            {
                if (note.RunId is { Length: > 0 } id)
                    byRun[id] = note.Text;
            }
        }

        return new RunLogbookViewModel(RunLogbook.Build(runs, today), null, byRun, today);
    }

    private static string Signed(int n) => n > 0 ? $"+{n:N0}" : n < 0 ? $"−{-n:N0}" : "±0";

    private static string Days(int n) => n == 1 ? "1 day" : $"{n:N0} days";
}
