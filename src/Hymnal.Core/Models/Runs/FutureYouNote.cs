namespace Hymnal.Core.Models.Runs;

/// <summary>A note the author leaves for their next session ("Future You"), kept per workspace.</summary>
public sealed record FutureYouNote
{
    public DateTimeOffset CreatedAt { get; init; }
    public string Text { get; init; } = "";

    /// <summary>The run this note was left after (<see cref="RunRecord.Id"/>), if any.</summary>
    public string? RunId { get; init; }

    /// <summary>The chapter the author was in when they left the note, if any.</summary>
    public string? ChapterPath { get; init; }
    public string? ChapterTitle { get; init; }

    /// <summary>The last non-blank line of that chapter when the note was left.</summary>
    public string? LastLine { get; init; }
}
