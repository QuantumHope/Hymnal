using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Infrastructure;

/// <summary>Story metadata stores for tests that build view models by hand.</summary>
internal static class TestMetadataStores
{
    /// <summary>
    /// A sidecar store over <paramref name="phases"/> and <paramref name="targets"/> (a fresh one when omitted),
    /// with real notes and beat-board services.
    /// </summary>
    public static SidecarStoryMetadataStore Sidecar(PhaseDataService phases, TargetsService? targets = null) =>
        new(phases,
            targets ?? new TargetsService(new MetadataStore()),
            new NotesService(new MetadataStore()),
            new BeatBoardService(new MetadataStore()));
}
