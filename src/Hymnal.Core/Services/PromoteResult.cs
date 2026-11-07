namespace Hymnal.Core.Services;

/// <summary>
/// Outcome of <see cref="StoryIdeaPromoter.PromoteAsync"/>: a <c>Result&lt;string&gt;</c> holding the new
/// story's path, plus whether the story file was created (true even when a later step failed).
/// </summary>
public readonly record struct PromoteResult
{
    public string? Value { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess { get; init; }

    /// <summary>True once the story file exists and is listed in Book.txt, whether or not setup finished.</summary>
    public bool FileCreated { get; init; }

    public static PromoteResult Ok(string storyPath) =>
        new() { Value = storyPath, IsSuccess = true, FileCreated = true };

    /// <summary>Failed before anything was written.</summary>
    public static PromoteResult Fail(string error) => new() { Error = error };

    /// <summary>The story file was created, but a later step failed.</summary>
    public static PromoteResult FailAfterCreate(string error) => new() { Error = error, FileCreated = true };
}
