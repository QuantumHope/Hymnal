namespace Hymnal.Core.Models;

public enum IdeaState { New, Developing, Used, Shelved }

/// <summary>A captured idea for a story that is not (yet) part of the manuscript.</summary>
public sealed record StoryIdea(
    string Id,
    string Title,
    string Premise,
    string Notes,
    IReadOnlyList<string> Tags,
    string? TargetPreset,
    IdeaState State,
    DateOnly CreatedAt,
    string? StoryId)
{
    public static StoryIdea Create(string title, string premise, DateOnly today) =>
        new(Guid.NewGuid().ToString("N"), title, premise, "", [], null, IdeaState.New, today, null);
}
