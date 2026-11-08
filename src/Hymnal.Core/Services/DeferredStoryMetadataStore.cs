using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Stands for a workspace's store while it is still being prepared (its embedded story is only known once the
/// chapter registry is loaded): every call waits for <paramref name="store"/> and then goes to it, so nothing
/// reads or writes a story's data in the wrong place in the meantime.
/// </summary>
public sealed class DeferredStoryMetadataStore(Task<IStoryMetadataStore> store) : IStoryMetadataStore
{
    public async Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories) =>
        await (await store.ConfigureAwait(false)).LoadPhaseDataAsync(root, stories).ConfigureAwait(false);

    public async Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update) =>
        await (await store.ConfigureAwait(false)).UpsertPhaseDataAsync(root, story, update).ConfigureAwait(false);

    public async Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories) =>
        await (await store.ConfigureAwait(false)).LoadTargetsAsync(root, stories).ConfigureAwait(false);

    public async Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target) =>
        await (await store.ConfigureAwait(false)).SetTargetAsync(root, story, target).ConfigureAwait(false);

    public async Task<string> LoadNotesAsync(string root, StoryRef story) =>
        await (await store.ConfigureAwait(false)).LoadNotesAsync(root, story).ConfigureAwait(false);

    public async Task SaveNotesAsync(string root, StoryRef story, string notes) =>
        await (await store.ConfigureAwait(false)).SaveNotesAsync(root, story, notes).ConfigureAwait(false);

    public async Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey) =>
        await (await store.ConfigureAwait(false)).LoadBeatsAsync(root, boardKey).ConfigureAwait(false);

    public async Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats) =>
        await (await store.ConfigureAwait(false)).SaveBeatsAsync(root, boardKey, beats).ConfigureAwait(false);
}
