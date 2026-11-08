using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Stores per-story data in the <c>.hymnal-data</c> sidecar files by delegating to the existing services, so
/// it writes exactly the same files and JSON as they do.
/// </summary>
public sealed class SidecarStoryMetadataStore(
    PhaseDataService phaseDataService,
    TargetsService targetsService,
    INotesService notesService,
    IBeatBoardService beatBoardService) : IStoryMetadataStore
{
    public Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories) =>
        phaseDataService.LoadAsync(root);

    public Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update) =>
        phaseDataService.UpsertAsync(root, story.Uuid, update);

    public Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories) =>
        targetsService.LoadAsync(root);

    public Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target) =>
        targetsService.UpsertAsync(root, story.Uuid, target);

    public Task<string> LoadNotesAsync(string root, StoryRef story) =>
        notesService.LoadAsync(INotesService.DeriveNotesPath(root, story.RelativePath));

    public Task SaveNotesAsync(string root, StoryRef story, string notes) =>
        notesService.SaveAsync(INotesService.DeriveNotesPath(root, story.RelativePath), notes);

    public Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey) =>
        beatBoardService.LoadAsync(root, boardKey);

    public Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats) =>
        beatBoardService.SaveAsync(root, boardKey, beats);
}
