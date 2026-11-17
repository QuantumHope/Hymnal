namespace Hymnal.Core.Models.Sprints;

/// <summary>
/// One finished writing sprint, as kept in the global writer profile. Only raw numbers are stored, never XP, so the
/// XP formula can change and be re-applied to history.
/// </summary>
public sealed record SprintSessionRecord
{
    public string Id { get; init; } = "";

    /// <summary>When the sprint started (UTC offset kept).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The author's local calendar date the sprint counts for (ISO 8601, e.g. "2026-11-17").</summary>
    public string LocalDate { get; init; } = "";

    public int PlannedSeconds { get; init; }
    public int ActualSeconds { get; init; }

    /// <summary>True when the timer ran out rather than the sprint being ended early.</summary>
    public bool Completed { get; init; }

    /// <summary>The workspace folder name, for display only.</summary>
    public string Workspace { get; init; } = "";

    public int WordsAdded { get; init; }
    public int WordsCut { get; init; }
    public int WordsNet { get; init; }

    /// <summary>How many chapters had edits in the sprint.</summary>
    public int Chapters { get; init; }
}
