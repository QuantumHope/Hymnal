using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Stands for a workspace's store while its story data is being moved between storages: reads go to
/// <see cref="Inner"/>, and every write is refused with <paramref name="reason"/> (writes throw
/// <see cref="IOException"/>, beat saves fail), so nothing is written to a location the move is about to clean up.
/// Only writes made in an async flow that <see cref="PermitWritesInThisFlow"/> was called in (and the flows it
/// starts) go through to <see cref="Inner"/>: the move's own final flush of pending edits.
/// </summary>
public sealed class RefusingStoryMetadataStore(IStoryMetadataStore inner, string reason) : IStoryMetadataStore
{
    private readonly AsyncLocal<bool> _permitted = new();

    /// <summary>The store reads go to.</summary>
    public IStoryMetadataStore Inner => inner;

    /// <summary>
    /// Lets writes made in the calling async flow reach <see cref="Inner"/> until the returned handle is disposed
    /// (in the same flow). Writes from every other flow stay refused.
    /// </summary>
    public IDisposable PermitWritesInThisFlow()
    {
        _permitted.Value = true;
        return new Permit(_permitted);
    }

    private bool Permitted => _permitted.Value;

    public Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories) =>
        inner.LoadPhaseDataAsync(root, stories);

    public Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update) =>
        Permitted ? inner.UpsertPhaseDataAsync(root, story, update) : Task.FromException(new IOException(reason));

    public Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories) =>
        inner.LoadTargetsAsync(root, stories);

    public Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target) =>
        Permitted ? inner.SetTargetAsync(root, story, target) : Task.FromException(new IOException(reason));

    public Task<string> LoadNotesAsync(string root, StoryRef story) => inner.LoadNotesAsync(root, story);

    public Task SaveNotesAsync(string root, StoryRef story, string notes) =>
        Permitted ? inner.SaveNotesAsync(root, story, notes) : Task.FromException(new IOException(reason));

    public Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey) =>
        inner.LoadBeatsAsync(root, boardKey);

    public Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats) =>
        Permitted ? inner.SaveBeatsAsync(root, boardKey, beats) : Task.FromResult(Result<Unit>.Fail(reason));

    private sealed class Permit(AsyncLocal<bool> permitted) : IDisposable
    {
        public void Dispose() => permitted.Value = false;
    }
}
