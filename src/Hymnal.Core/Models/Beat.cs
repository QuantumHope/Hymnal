namespace Hymnal.Core.Models;

/// <summary>One planning card on a beat board. <paramref name="ScenePath"/> is a ChapterNode.RelativePath or null.</summary>
public sealed record Beat(string Id, string Title, string Note, string? ScenePath)
{
    public static Beat Create(string title, string note = "") =>
        new(Guid.NewGuid().ToString(), title, note, null);
}
