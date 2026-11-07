namespace Hymnal.Core.Models;

/// <summary>
/// One planning card on a beat board. <paramref name="ScenePath"/> is a ChapterNode.RelativePath or null;
/// <paramref name="SceneId"/> is that scene's chapter-registry UUID (null in files written before it was
/// stored), which keeps the link when the scene is renamed.
/// </summary>
public sealed record Beat(string Id, string Title, string Note, string? ScenePath, string? SceneId = null)
{
    public static Beat Create(string title, string note = "") =>
        new(Guid.NewGuid().ToString(), title, note, null);
}
