namespace Hymnal.Core.Models;

/// <summary>
/// One status bucket of a pie/summary chart. <paramref name="Label"/> is the profile's display
/// label for <paramref name="Status"/>; null falls back to the enum name.
/// </summary>
public record StatusCount(ChapterStatus Status, int Count, string? Label = null)
{
    public string DisplayLabel => Label ?? Status.ToString();
}
