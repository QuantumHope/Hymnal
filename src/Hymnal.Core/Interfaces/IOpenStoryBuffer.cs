using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// The story file currently open in the editor, as seen by <see cref="Services.StoryFileWriter"/>. Metadata
/// writes for an open file go through the buffer so unsaved prose is never overwritten or lost.
/// <para>
/// Threading: every member may be called on any thread (the writer runs on thread-pool continuations). An
/// implementation bound to UI state must marshal to its own UI thread itself.
/// </para>
/// </summary>
public interface IOpenStoryBuffer
{
    /// <summary>
    /// True when the buffer holds the file at <paramref name="absolutePath"/>: a normalized full path, asked first
    /// as given and, when it is a symbolic link, again as the link's final target.
    /// </summary>
    bool HoldsFile(string absolutePath);

    /// <summary>The metadata block the buffer holds for its file, or null when the file has none.</summary>
    EmbeddedStoryMetadata? CurrentMetadata { get; }

    /// <summary>
    /// Replaces the held metadata and saves the file through the normal editor save path, keeping unsaved prose.
    /// Null removes the block (used when migrating the story's data out of the file).
    /// </summary>
    Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata);
}
