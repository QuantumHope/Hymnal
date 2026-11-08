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
/// read-only, as with sidecar boards. An existing block <c>id</c> is kept; a new block takes the story's UUID.
/// </para>
/// </summary>
public sealed class EmbeddedStoryMetadataStore(SidecarStoryMetadataStore sidecar, StoryFileWriter writer)
    : IStoryMetadataStore
{
    private const string IsoDate = "yyyy-MM-dd";

    private volatile StoryRef? _primary;

    public void SetPrimaryStory(StoryRef? primary) => _primary = primary;

    // ── Phase data ───────────────────────────────────────────────────────────

    public async Task<Dictionary<string, PhaseData>> LoadPhaseDataAsync(string root, IReadOnlyList<StoryRef> stories)
    {
        var result = await sidecar.LoadPhaseDataAsync(root, stories).ConfigureAwait(false);
        if (PrimaryIn(stories) is { } primary)
        {
            result.Remove(primary.Uuid);
            if (ToPhaseData(await ReadBlockAsync(primary).ConfigureAwait(false)) is { } phaseData)
                result[primary.Uuid] = phaseData;
        }
        return result;
    }

    public Task UpsertPhaseDataAsync(string root, StoryRef story, Func<PhaseData?, PhaseData> update) =>
        IsPrimary(story)
            ? UpdateBlockAsync(story, block => WithPhaseData(block, update(ToPhaseData(block))))
            : sidecar.UpsertPhaseDataAsync(root, story, update);

    // ── Targets ──────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, WordCountTarget>> LoadTargetsAsync(string root, IReadOnlyList<StoryRef> stories)
    {
        var result = await sidecar.LoadTargetsAsync(root, stories).ConfigureAwait(false);
        if (PrimaryIn(stories) is { } primary)
        {
            result.Remove(primary.Uuid);
            if ((await ReadBlockAsync(primary).ConfigureAwait(false))?.Target is { } target)
                result[primary.Uuid] = target;
        }
        return result;
    }

    public Task SetTargetAsync(string root, StoryRef story, WordCountTarget? target) =>
        IsPrimary(story)
            ? UpdateBlockAsync(story, block => block with { Target = target })
            : sidecar.SetTargetAsync(root, story, target);

    // ── Notes ────────────────────────────────────────────────────────────────

    public async Task<string> LoadNotesAsync(string root, StoryRef story) =>
        IsPrimary(story)
            ? (await ReadBlockAsync(story).ConfigureAwait(false))?.Notes ?? ""
            : await sidecar.LoadNotesAsync(root, story).ConfigureAwait(false);

    public Task SaveNotesAsync(string root, StoryRef story, string notes) =>
        IsPrimary(story)
            ? UpdateBlockAsync(story, block => block with { Notes = string.IsNullOrEmpty(notes) ? null : notes })
            : sidecar.SaveNotesAsync(root, story, notes);

    // ── Beats ────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<Beat>>> LoadBeatsAsync(string root, string boardKey)
    {
        if (StoryBoardPrimary(boardKey) is not { } primary)
            return await sidecar.LoadBeatsAsync(root, boardKey).ConfigureAwait(false);

        // A damaged block must never be replaced by a beat save, so the board loads as an error (read-only).
        var file = await writer.ReadFileAsync(primary.AbsolutePath).ConfigureAwait(false);
        if (!file.IsSuccess)
            return Result<IReadOnlyList<Beat>>.Fail(file.Error!);
        if (file.Value!.HadMalformedBlock)
            return Result<IReadOnlyList<Beat>>.Fail(
                $"The Hymnal data at the end of {Path.GetFileName(primary.AbsolutePath)} could not be read.");

        var current = await writer.ReadMetadataAsync(primary.AbsolutePath).ConfigureAwait(false);
        if (!current.IsSuccess)
            return Result<IReadOnlyList<Beat>>.Fail(current.Error!);

        return ValidateBeats(current.Value?.Beats, Path.GetFileName(primary.AbsolutePath));
    }

    public async Task<Result<Unit>> SaveBeatsAsync(string root, string boardKey, IReadOnlyList<Beat> beats)
    {
        if (StoryBoardPrimary(boardKey) is not { } primary)
            return await sidecar.SaveBeatsAsync(root, boardKey, beats).ConfigureAwait(false);

        if (beats is null || beats.Any(b => b is null))
            return Result<Unit>.Fail("Beat board cannot be saved with missing beats.");

        var saved = beats.ToList();
        return await writer.UpdateMetadataAsync(
                primary.AbsolutePath,
                current => Seed(current, primary) with { Beats = saved.Count == 0 ? null : saved })
            .ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsPrimary(StoryRef story) =>
        _primary is { } primary && string.Equals(primary.RelativePath, story.RelativePath, StringComparison.Ordinal);

    private StoryRef? PrimaryIn(IReadOnlyList<StoryRef> stories) => stories.FirstOrDefault(IsPrimary);

    private StoryRef? StoryBoardPrimary(string boardKey) =>
        string.Equals(boardKey, BeatBoardService.ShortStoryBoardKey, StringComparison.Ordinal) ? _primary : null;

    /// <summary>The story's block, or null when it has none, it is malformed or the file can't be read.</summary>
    private async Task<EmbeddedStoryMetadata?> ReadBlockAsync(StoryRef story)
    {
        var read = await writer.ReadMetadataAsync(story.AbsolutePath).ConfigureAwait(false);
        return read.IsSuccess ? read.Value : null;
    }

    /// <summary>Applies the pure <paramref name="change"/> to the story's block; throws when it can't be stored.</summary>
    private async Task UpdateBlockAsync(StoryRef story, Func<EmbeddedStoryMetadata, EmbeddedStoryMetadata> change)
    {
        var result = await writer.UpdateMetadataAsync(story.AbsolutePath, current => change(Seed(current, story)))
            .ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new IOException(result.Error);
    }

    /// <summary>The current block, or a new one carrying the story's UUID; an existing id is kept.</summary>
    private static EmbeddedStoryMetadata Seed(EmbeddedStoryMetadata? current, StoryRef story) =>
        current is null
            ? new EmbeddedStoryMetadata(story.Uuid, null, null, null, null, null, null, null)
            : current with { Id = current.Id ?? story.Uuid };

    private static PhaseData? ToPhaseData(EmbeddedStoryMetadata? block)
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

    private static EmbeddedStoryMetadata WithPhaseData(EmbeddedStoryMetadata block, PhaseData data) =>
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

    /// <summary>Applies <see cref="BeatBoardService"/>'s load rules to block beats.</summary>
    private static Result<IReadOnlyList<Beat>> ValidateBeats(IReadOnlyList<Beat>? blockBeats, string fileName)
    {
        var beats = new List<Beat>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in blockBeats ?? [])
        {
            if (b is null)
                return Result<IReadOnlyList<Beat>>.Fail($"The story board in {fileName} contains an empty beat entry.");
            if (string.IsNullOrWhiteSpace(b.Id) || b.Title is null)
                return Result<IReadOnlyList<Beat>>.Fail($"The story board in {fileName} contains a beat without an id or title.");
            if (!seenIds.Add(b.Id))
                return Result<IReadOnlyList<Beat>>.Fail($"The story board in {fileName} contains duplicate beat id '{b.Id}'.");
            beats.Add(b with { Note = b.Note ?? "" });
        }
        return Result<IReadOnlyList<Beat>>.Ok(beats);
    }
}
