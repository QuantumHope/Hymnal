using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

/// <summary>A chapter/story file as the metadata store sees it.</summary>
/// <param name="Uuid">The chapter-registry UUID (for an embedded story, its block id).</param>
/// <param name="RelativePath">Workspace-relative, forward-slash path (the <see cref="ChapterNode"/> key).</param>
/// <param name="AbsolutePath">The file's absolute path.</param>
public sealed record StoryRef(string Uuid, string RelativePath, string AbsolutePath);

/// <summary>
/// Per-story status/schedule, target, notes and beats, wherever they are stored: the <c>.hymnal-data</c>
/// sidecar files, or the trailing block of a single-file story (embedded storage).
/// <para>
/// Load methods return dictionaries keyed by <see cref="StoryRef.Uuid"/>; a story without data has no entry.
/// Write methods throw <see cref="IOException"/> (or the sidecar services' exceptions) when the data cannot be
/// stored, as the sidecar services always have. Board moves are not part of this store; they stay on
/// <see cref="IBeatBoardService.MoveBoardAsync"/>, which only applies to sidecar boards.
/// </para>
/// </summary>
public interface IStoryMetadataStore
{
    Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories);

    /// <summary>
    /// Replaces the story's phase data with <paramref name="update"/>(current). The delegate may run while a
    /// file lock is held: it must be pure (no I/O, no calls back into this store).
    /// </summary>
    Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update);

    Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories);

    /// <summary>Sets the story's target, or removes it when <paramref name="target"/> is null.</summary>
    Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target);

    /// <summary>The story's notes, or an empty string when it has none.</summary>
    Task<string> LoadNotesAsync(string root, StoryRef story);

    Task SaveNotesAsync(string root, StoryRef story, string notes);

    Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey);

    Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats);
}
