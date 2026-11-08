using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// The story file currently open in the editor, as seen by <see cref="Services.StoryFileWriter"/>. Metadata
/// writes for an open file go through the buffer so unsaved prose is never overwritten or lost.
/// </summary>
public interface IOpenStoryBuffer
{
    /// <summary>True when the buffer holds the file at <paramref name="absolutePath"/> (a full path).</summary>
    bool HoldsFile(string absolutePath);

    /// <summary>The metadata block the buffer holds for its file, or null when the file has none.</summary>
    EmbeddedStoryMetadata? CurrentMetadata { get; }

    /// <summary>
    /// Replaces the held metadata and saves the file through the normal editor save path, keeping unsaved prose.
    /// Null removes the block (used when migrating the story's data out of the file).
    /// </summary>
    Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata);
}
