using System.Globalization;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Embedded storage: the primary story's status/schedule, target, notes and story board
/// (<see cref="BeatBoardService.ShortStoryBoardKey"/>) live in its file's trailing block, read and written
/// through <see cref="StoryFileWriter"/>. Every other file and board key uses <paramref name="sidecar"/>.
/// <para>
/// For the primary story the block is the only source: a sidecar entry under its UUID is ignored on load and
/// never written. Block values are guarded on read: an undefined status means none, and an invalid story board
/// (empty, id-less, title-less or duplicate-id beats, or a malformed block) fails to load so the board stays
/// read-only, as with sidecar boards; a story-board save onto a malformed block fails too. An existing block
/// <c>id</c> is kept; a new block takes the primary story's UUID.
/// </para>
/// <para>
/// Until the workspace sets the primary (<see cref="SetPrimaryStory"/>), and when it is unavailable
/// (<see cref="SetPrimaryUnavailable"/>, <see cref="SetNoStoryFile"/>), the story's data is never routed to the
/// sidecar instead: its writes throw <see cref="IOException"/> (story-board saves fail), its notes load throws
/// and its story board loads as failed, all with the reason; its phase data and target load as none. With no
/// primary at all that applies to every story. Loading the phase data or target of a primary that can't be read
/// throws <see cref="IOException"/>, like the sidecar loads.
/// </para>
/// </summary>
public sealed class EmbeddedStoryMetadataStore(SidecarStoryMetadataStore sidecar, StoryFileWriter writer)
    : IStoryMetadataStore
{
    private const string IsoDate = "yyyy-MM-dd";

    /// <summary>Why every story's data is refused before the workspace has set the primary.</summary>
    public const string NotLoadedMessage = "This story's data couldn't be loaded. Reopen the project to try again.";

    /// <summary>Why every story's data is refused when Book.txt lists no story file.</summary>
    public const string NoStoryFileMessage = "Add a story file to keep beats in it.";

    /// <summary>The primary story (null: none known) and, when its data can't be used, why.</summary>
    private sealed record PrimaryState(StoryRef? Primary, string? Unavailable);

    private volatile PrimaryState _state = new(null, NotLoadedMessage);

    /// <summary>Sets the story whose data lives in its own file.</summary>
    public void SetPrimaryStory(StoryRef primary) => _state = new(primary, null);

    /// <summary>
    /// Sets the primary story whose file can't be read: its data is refused with <paramref name="reason"/>
    /// (never routed to the sidecar) until a later <see cref="SetPrimaryStory"/>.
    /// </summary>
    public void SetPrimaryUnavailable(StoryRef primary, string reason) => _state = new(primary, reason);

    /// <summary>Book.txt lists no story file: every story's data is refused.</summary>
    public void SetNoStoryFile() => _state = new(null, NoStoryFileMessage);

    /// <summary>The story whose data lives in its own file (possibly unavailable); null when none is known.</summary>
    public StoryRef? Primary => _state.Primary;

    /// <summary>True when Book.txt lists no story file, so no story's data is embedded.</summary>
    public bool HasNoStoryFile => _state is { Primary: null, Unavailable: NoStoryFileMessage };

    // ── Phase data ───────────────────────────────────────────────────────────

    public async Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories)
    {
        var state = _state;
        var result = await sidecar.LoadPhaseDataAsync(root, stories).ConfigureAwait(false);
        if (DropPrimaryEntries(state, result, stories) is { } primary && state.Unavailable is null
            && ToPhaseData(await ReadBlockAsync(primary).ConfigureAwait(false)) is { } phaseData)
            result[primary.Uuid] = phaseData;
        return result;
    }

    public Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update)
    {
        var state = _state;
        return RouteOf(state, story) switch
        {
            Route.Block => UpdateBlockAsync(state.Primary!, block => WithPhaseData(block, update(ToPhaseData(block)))),
            Route.Sidecar => sidecar.UpsertPhaseDataAsync(root, story, update),
            _ => throw new IOException(state.Unavailable)
        };
    }

    // ── Targets ──────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories)
    {
        var state = _state;
        var result = await sidecar.LoadTargetsAsync(root, stories).ConfigureAwait(false);
        if (DropPrimaryEntries(state, result, stories) is { } primary && state.Unavailable is null
            && (await ReadBlockAsync(primary).ConfigureAwait(false))?.Target is { } target)
            result[primary.Uuid] = target;
        return result;
    }

    public Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target)
    {
        var state = _state;
        return RouteOf(state, story) switch
        {
            Route.Block => UpdateBlockAsync(state.Primary!, block => block with { Target = target }),
            Route.Sidecar => sidecar.SetTargetAsync(root, story, target),
            _ => throw new IOException(state.Unavailable)
        };
    }

    // ── Notes ────────────────────────────────────────────────────────────────

    public async Task<string> LoadNotesAsync(string root, StoryRef story)
    {
        var state = _state;
        return RouteOf(state, story) switch
        {
            Route.Block => (await ReadBlockAsync(state.Primary!).ConfigureAwait(false))?.Notes ?? "",
            Route.Sidecar => await sidecar.LoadNotesAsync(root, story).ConfigureAwait(false),
            _ => throw new IOException(state.Unavailable)
        };
    }

    public Task SaveNotesAsync(string root, StoryRef story, string notes)
    {
        var state = _state;
        return RouteOf(state, story) switch
        {
            Route.Block => UpdateBlockAsync(
                state.Primary!, block => block with { Notes = string.IsNullOrEmpty(notes) ? null : notes }),
            Route.Sidecar => sidecar.SaveNotesAsync(root, story, notes),
            _ => throw new IOException(state.Unavailable)
        };
    }

    // ── Beats ────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey)
    {
        var state = _state;
        if (!IsStoryBoard(boardKey))
            return await sidecar.LoadBeatsAsync(root, boardKey).ConfigureAwait(false);
        if (state.Unavailable is { } unavailable)
            return Result<IReadOnlyList<Beat>>.Fail(unavailable);
        var primary = state.Primary!;

        // A damaged block must never be replaced by a beat save, so the board loads as an error (read-only).
        if (await CheckBlockReadableAsync(primary).ConfigureAwait(false) is { } error)
            return Result<IReadOnlyList<Beat>>.Fail(error);

        var current = await writer.ReadMetadataAsync(primary.AbsolutePath).ConfigureAwait(false);
        if (!current.IsSuccess)
            return Result<IReadOnlyList<Beat>>.Fail(current.Error!);

        return BeatBoardService.ValidateBeats(
            current.Value?.Beats, $"The story board in {Path.GetFileName(primary.AbsolutePath)}");
    }

    public async Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats)
    {
        var state = _state;
        if (!IsStoryBoard(boardKey))
            return await sidecar.SaveBeatsAsync(root, boardKey, beats).ConfigureAwait(false);
        if (state.Unavailable is { } unavailable)
            return Result<Unit>.Fail(unavailable);
        var primary = state.Primary!;

        if (beats is null || beats.Any(b => b is null))
            return Result<Unit>.Fail("Beat board cannot be saved with missing beats.");

        if (await CheckBlockReadableAsync(primary).ConfigureAwait(false) is { } error)
            return Result<Unit>.Fail(error);

        var saved = beats.ToList();
        return await writer.UpdateMetadataAsync(
                primary.AbsolutePath,
                current => Seed(current, primary) with { Beats = saved.Count == 0 ? null : saved })
            .ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private enum Route { Block, Sidecar, Refused }

    /// <summary>
    /// Where <paramref name="story"/>'s data goes: the primary's block, the sidecar (any other file), or nowhere
    /// (the primary is unavailable, or no primary is known so any story might be it).
    /// </summary>
    private static Route RouteOf(PrimaryState state, StoryRef story)
    {
        if (state.Primary is not { } primary)
            return Route.Refused;
        if (!IsSameFile(primary, story))
            return Route.Sidecar;
        return state.Unavailable is null ? Route.Block : Route.Refused;
    }

    /// <summary>Matches by workspace-relative path, ignoring case like the chapter registry and the sidebar.</summary>
    private static bool IsSameFile(StoryRef primary, StoryRef story) =>
        string.Equals(primary.RelativePath, story.RelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the primary story's sidecar entries (under the primary's UUID and under the UUID its entry in
    /// <paramref name="stories"/> carries, which differ while a block id is being adopted) and returns that
    /// entry, or null when there is no primary or <paramref name="stories"/> doesn't include it.
    /// </summary>
    private static StoryRef? DropPrimaryEntries<T>(
        PrimaryState state, Dictionary<string, T> sidecarEntries, IReadOnlyList<StoryRef> stories)
    {
        if (state.Primary is not { } primary)
            return null;

        sidecarEntries.Remove(primary.Uuid);
        var listed = stories.FirstOrDefault(story => IsSameFile(primary, story));
        if (listed is not null)
            sidecarEntries.Remove(listed.Uuid);
        return listed;
    }

    private static bool IsStoryBoard(string boardKey) =>
        string.Equals(boardKey, BeatBoardService.ShortStoryBoardKey, StringComparison.Ordinal);

    /// <summary>
    /// The story's block, or null when it has none or it is malformed. Throws <see cref="IOException"/> when the
    /// file can't be read, as the sidecar loads do, so a caller never mistakes unreadable data for none.
    /// </summary>
    private async Task<EmbeddedStoryMetadata?> ReadBlockAsync(StoryRef story)
    {
        var read = await writer.ReadMetadataAsync(story.AbsolutePath).ConfigureAwait(false);
        return read.IsSuccess ? read.Value : throw new IOException(read.Error);
    }

    /// <summary>The error when the file can't be read or its block is malformed; null when it is fine.</summary>
    private async Task<string?> CheckBlockReadableAsync(StoryRef story)
    {
        var file = await writer.ReadFileAsync(story.AbsolutePath).ConfigureAwait(false);
        if (!file.IsSuccess)
            return file.Error;
        return file.Value!.HadMalformedBlock
            ? $"The Hymnal data at the end of {Path.GetFileName(story.AbsolutePath)} could not be read."
            : null;
    }

    /// <summary>
    /// Applies the pure <paramref name="change"/> to the primary's block (a new block takes the primary's UUID);
    /// throws when it can't be stored.
    /// </summary>
    private async Task UpdateBlockAsync(StoryRef story, Func<EmbeddedStoryMetadata, EmbeddedStoryMetadata> change)
    {
        var result = await writer.UpdateMetadataAsync(story.AbsolutePath, current => change(Seed(current, story)))
            .ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new IOException(result.Error);
    }

    /// <summary>
    /// The current block, or a new one carrying the story's UUID; an existing id is kept. A story whose UUID
    /// isn't known yet (empty) leaves the id unset for a later write to fill.
    /// </summary>
    private static EmbeddedStoryMetadata Seed(EmbeddedStoryMetadata? current, StoryRef story)
    {
        var uuid = string.IsNullOrWhiteSpace(story.Uuid) ? null : story.Uuid;
        return current is null
            ? new EmbeddedStoryMetadata(uuid, null, null, null, null, null, null, null)
            : current with { Id = current.Id ?? uuid };
    }

    internal static PhaseData? ToPhaseData(EmbeddedStoryMetadata? block)
    {
        if (block is null)
            return null;

        ChapterStatus? status = block.Status is { } s && Enum.IsDefined(s) ? s : null;
        if (status is null && block.PhaseStartDate is null && block.PhaseEndDate is null && block.Schedule is null)
            return null;

        return new PhaseData
        {
            // A block with dates but no (valid) status reads like a sidecar entry without one: the enum default.
            Status = status ?? default,
            PhaseStartDate = ToIso(block.PhaseStartDate),
            PhaseEndDate = ToIso(block.PhaseEndDate),
            Schedule = block.Schedule?
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    internal static EmbeddedStoryMetadata WithPhaseData(EmbeddedStoryMetadata block, PhaseData data) =>
        block with
        {
            Status = data.Status,
            // A legacy date string that isn't ISO yyyy-MM-dd is stored as no date rather than failing the write.
            PhaseStartDate = FromIso(data.PhaseStartDate),
            PhaseEndDate = FromIso(data.PhaseEndDate),
            Schedule = data.Schedule is null ? null : new Dictionary<string, PhaseScheduleEntry>(data.Schedule)
        };

    private static string? ToIso(DateOnly? date) => date?.ToString(IsoDate, CultureInfo.InvariantCulture);

    private static DateOnly? FromIso(string? date) =>
        DateOnly.TryParseExact(date, IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
