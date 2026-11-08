using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Stands for a workspace's store while its story data is being moved between storages: reads go to
/// <see cref="Inner"/>, and every write is refused with <paramref name="reason"/> (writes throw
/// <see cref="IOException"/>, beat saves fail), so nothing is written to a location the move is about to clean up.
/// </summary>
public sealed class RefusingStoryMetadataStore(IStoryMetadataStore inner, string reason) : IStoryMetadataStore
{
    /// <summary>The store reads go to.</summary>
    public IStoryMetadataStore Inner => inner;

    public Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories) =>
        inner.LoadPhaseDataAsync(root, stories);

    public Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update) =>
        Task.FromException(new IOException(reason));

    public Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories) =>
        inner.LoadTargetsAsync(root, stories);

    public Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target) =>
        Task.FromException(new IOException(reason));

    public Task<string> LoadNotesAsync(string root, StoryRef story) => inner.LoadNotesAsync(root, story);

    public Task SaveNotesAsync(string root, StoryRef story, string notes) =>
        Task.FromException(new IOException(reason));

    public Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey) =>
        inner.LoadBeatsAsync(root, boardKey);

    public Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats) =>
        Task.FromResult(Result<Unit>.Fail(reason));
}
