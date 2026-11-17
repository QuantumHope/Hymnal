namespace Hymnal.Core.Models.Runs;

/// <summary>
/// One finished writing run, as kept in the global writer profile. Only raw numbers are stored, never XP, so the
/// XP formula can change and be re-applied to history.
/// </summary>
public sealed record RunRecord
{
    public string Id { get; init; } = "";

    /// <summary>When the run started (UTC offset kept).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The author's local calendar date the run counts for (ISO 8601, e.g. "2026-11-17").</summary>
    public string LocalDate { get; init; } = "";

    public int PlannedSeconds { get; init; }
    public int ActualSeconds { get; init; }

    /// <summary>True when the timer ran out rather than the run being ended early.</summary>
    public bool Completed { get; init; }

    /// <summary>The workspace folder name, for display only.</summary>
    public string Workspace { get; init; } = "";

    /// <summary>
    /// The workspace's full path, so the Logbook can find the note left after this run. Null in runs recorded
    /// before it was kept.
    /// </summary>
    public string? WorkspacePath { get; init; }

    public int WordsAdded { get; init; }
    public int WordsCut { get; init; }
    public int WordsNet { get; init; }

    /// <summary>How many chapters had edits in the run.</summary>
    public int Chapters { get; init; }

    /// <summary>
    /// Words added in each minute of the run, in order (the last entry may cover a part minute). Null in runs
    /// recorded before it was kept.
    /// </summary>
    public int[]? MinuteAdded { get; init; }

    /// <summary>Words cut in each minute, matching <see cref="MinuteAdded"/>.</summary>
    public int[]? MinuteCut { get; init; }
}
