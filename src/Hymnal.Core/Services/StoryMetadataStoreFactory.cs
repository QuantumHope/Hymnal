using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public static class StoryMetadataStoreFactory
{
    /// <summary>
    /// The embedded store for a Story project set to embedded storage; the sidecar store for everything else.
    /// </summary>
    public static IStoryMetadataStore Create(
        ProjectSettings settings, SidecarStoryMetadataStore sidecar, StoryFileWriter writer) =>
        settings is { Kind: ProjectKind.Story, Storage: StorageMode.Embedded }
            ? new EmbeddedStoryMetadataStore(sidecar, writer)
            : sidecar;

    /// <summary>
    /// The file whose data is embedded: the first (in Book.txt order) that has a block, else the first file;
    /// null when there are no files.
    /// </summary>
    public static StoryRef? ResolvePrimary(
        IReadOnlyList<StoryRef> chapterFiles, Func<StoryRef, EmbeddedStoryMetadata?> blockOf) =>
        chapterFiles.FirstOrDefault(file => blockOf(file) is not null) ?? chapterFiles.FirstOrDefault();
}
