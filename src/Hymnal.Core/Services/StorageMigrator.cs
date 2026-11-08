using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Moves a Short Story's own data — status and schedule, target, notes and the story board — between the
/// <c>.hymnal-data</c> sidecar files and its file's trailing block, when the author turns "Keep everything in the
/// story file" on or off. Only that story's entries move; other stories, boards and project data stay put.
/// <para>
/// A migration has three steps, and the caller saves <c>project.json</c> between the last two:
/// <see cref="WriteDestinationAsync"/> makes the destination mirror the source (the storage the project is in
/// now, which is the source of truth): a field the source has no value for is cleared there too, so a stale copy
/// left by an earlier migration never resurfaces. Every source read happens before anything is written, and a
/// failed read aborts, so a field is never cleared because its source couldn't be read. Saving
/// <c>project.json</c> in the new mode is the commit point; then <see cref="CleanupSourceAsync"/> removes the old
/// copies, best effort. Whatever step is interrupted, the mode <c>project.json</c> names has all the data: the
/// destination is complete before the commit, and the source is only removed after it. Leftover copies are
/// ignored in the new mode and replaced by the next migration's mirror.
/// </para>
/// <para>
/// The story file is written through <see cref="StoryFileWriter"/>: when it is open in the editor, the block
/// change is saved through the editor, together with any unsaved prose. Pending edits held by view models
/// (notes, beats, schedule) must be saved before migrating.
/// </para>
/// </summary>
public sealed class StorageMigrator(
    PhaseDataService phaseDataService,
    TargetsService targetsService,
    INotesService notesService,
    IBeatBoardService beatBoardService,
    StoryFileWriter writer)
{
    private const string StoryBoard = BeatBoardService.ShortStoryBoardKey;

    /// <summary>
    /// Step 1: writes <paramref name="primary"/>'s data, read from <paramref name="from"/> storage, to
    /// <paramref name="to"/> storage, leaving the source untouched. Does nothing when they are the same. On
    /// failure nothing was removed and the project's current mode still shows all the data.
    /// </summary>
    public async Task<Result<Unit>> WriteDestinationAsync(string root, StoryRef primary, StorageMode from, StorageMode to)
    {
        if (from == to)
            return Result<Unit>.Ok(Unit.Default);
        if (string.IsNullOrWhiteSpace(primary.Uuid))
            return Result<Unit>.Fail("The story has no identity yet. Reopen the project and try again.");

        try
        {
            return to == StorageMode.Embedded
                ? await WriteBlockAsync(root, primary).ConfigureAwait(false)
                : await WriteSidecarAsync(root, primary).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDataException(ex))
        {
            // Only reads throw here (each write step reports its own failure), and all reads come first.
            return Result<Unit>.Fail($"The story's data couldn't be read: {ex.Message} Nothing was changed.");
        }
    }

    /// <summary>
    /// Step 2, after <c>project.json</c> names <paramref name="to"/>: removes the copies left in
    /// <paramref name="from"/> storage. Tries every copy even when one fails; a failure lists what was left, which
    /// is harmless (ignored in the new mode).
    /// </summary>
    public async Task<Result<Unit>> CleanupSourceAsync(string root, StoryRef primary, StorageMode from, StorageMode to)
    {
        if (from == to)
            return Result<Unit>.Ok(Unit.Default);

        var steps = from == StorageMode.Sidecar
            ? new (string Name, Func<Task> Remove)[]
            {
                ("status and schedule", () => phaseDataService.RemoveAsync(root, primary.Uuid)),
                ("target", () => targetsService.UpsertAsync(root, primary.Uuid, null)),
                ("notes", () => notesService.DeleteAsync(NotesPath(root, primary))),
                ("story board", () => ThrowOnFailAsync(beatBoardService.DeleteBoardAsync(root, StoryBoard)))
            }
            : [("data block", () => ThrowOnFailAsync(writer.RemoveMetadataAsync(primary.AbsolutePath)))];

        var left = new List<string>();
        foreach (var (name, remove) in steps)
        {
            if (await TryAsync(remove).ConfigureAwait(false) is { } error)
                left.Add($"{name} ({error})");
        }

        return left.Count == 0
            ? Result<Unit>.Ok(Unit.Default)
            : Result<Unit>.Fail($"Old copies couldn't be removed: {string.Join("; ", left)}.");
    }

    // ── Sidecar → embedded ───────────────────────────────────────────────────

    private async Task<Result<Unit>> WriteBlockAsync(string root, StoryRef primary)
    {
        var fileName = Path.GetFileName(primary.AbsolutePath);
        var file = await writer.TryReadFileAsync(primary.AbsolutePath).ConfigureAwait(false);
        if (file.NotUtf8)
            return Result<Unit>.Fail(
                $"{fileName} isn't saved as UTF-8 text, so Hymnal can't keep its data in it. Nothing was changed.");
        if (!file.Result.IsSuccess)
            return Result<Unit>.Fail($"{file.Result.Error} Nothing was changed.");

        // Every read before any write: a read that throws aborts the migration with nothing written.
        (await phaseDataService.LoadAsync(root).ConfigureAwait(false)).TryGetValue(primary.Uuid, out var phase);
        (await targetsService.LoadAsync(root).ConfigureAwait(false)).TryGetValue(primary.Uuid, out var target);
        var notes = await notesService.LoadAsync(NotesPath(root, primary)).ConfigureAwait(false);
        var beats = await beatBoardService.LoadAsync(root, StoryBoard).ConfigureAwait(false);
        if (!beats.IsSuccess)
            return Result<Unit>.Fail($"The story board couldn't be read: {beats.Error} Nothing was changed.");

        var block = new EmbeddedStoryMetadata(
            primary.Uuid, null, null, null, null,
            target,
            string.IsNullOrEmpty(notes) ? null : notes,
            beats.Value!.Count == 0 ? null : beats.Value!.ToList());
        if (phase is not null)
            block = EmbeddedStoryMetadataStore.WithPhaseData(block, phase);

        var written = await writer.UpdateMetadataAsync(primary.AbsolutePath, _ => block).ConfigureAwait(false);
        return written.IsSuccess
            ? written
            : Result<Unit>.Fail(
                $"The story's data couldn't be written to {fileName}: {written.Error} Nothing was removed.");
    }

    // ── Embedded → sidecar ───────────────────────────────────────────────────

    private async Task<Result<Unit>> WriteSidecarAsync(string root, StoryRef primary)
    {
        var fileName = Path.GetFileName(primary.AbsolutePath);
        var file = await writer.TryReadFileAsync(primary.AbsolutePath).ConfigureAwait(false);
        // A file that isn't UTF-8 can't hold a block Hymnal wrote: there is nothing to move, and the sidecar is
        // left as it is.
        if (file.NotUtf8)
            return Result<Unit>.Ok(Unit.Default);
        if (!file.Result.IsSuccess)
            return Result<Unit>.Fail($"{file.Result.Error} Nothing was changed.");
        if (file.Result.Value!.HadMalformedBlock)
            return Result<Unit>.Fail(
                $"The Hymnal data at the end of {fileName} could not be read. Fix or remove it, then try again. "
                + "Nothing was changed.");

        // The buffer's metadata when the file is open in the editor, which is the latest.
        var read = await writer.ReadMetadataAsync(primary.AbsolutePath).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result<Unit>.Fail($"{read.Error} Nothing was changed.");
        var block = read.Value;

        var phase = EmbeddedStoryMetadataStore.ToPhaseData(block);
        var notes = block?.Notes;
        var beats = block?.Beats is { Count: > 0 } b ? b : null;
        if (beats is not null && BeatBoardService.ValidateBeats(beats, $"The story board in {fileName}") is
                { IsSuccess: false } invalid)
            return Result<Unit>.Fail($"{invalid.Error} Nothing was changed.");

        var copied = new List<string>();
        var steps = new (string Name, Func<Task> Write)[]
        {
            ("status and schedule", () => phase is null
                ? phaseDataService.RemoveAsync(root, primary.Uuid)
                : phaseDataService.UpsertAsync(root, primary.Uuid, _ => phase)),
            ("target", () => targetsService.UpsertAsync(root, primary.Uuid, block?.Target)),
            ("notes", () => string.IsNullOrEmpty(notes)
                ? notesService.DeleteAsync(NotesPath(root, primary))
                : notesService.SaveAsync(NotesPath(root, primary), notes)),
            ("story board", () => ThrowOnFailAsync(beats is null
                ? beatBoardService.DeleteBoardAsync(root, StoryBoard)
                : beatBoardService.SaveAsync(root, StoryBoard, beats)))
        };
        foreach (var (name, write) in steps)
        {
            if (await TryAsync(write).ConfigureAwait(false) is { } error)
                return Result<Unit>.Fail(
                    $"The story's {name} couldn't be written to project data: {error}"
                    + (copied.Count > 0 ? $" Already copied: {string.Join(", ", copied)}." : "")
                    + $" Nothing was removed from {fileName}.");
            copied.Add(name);
        }
        return Result<Unit>.Ok(Unit.Default);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsDataException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException;

    /// <summary>Runs <paramref name="step"/>; returns its error message, or null when it succeeded.</summary>
    private static async Task<string?> TryAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (IsDataException(ex))
        {
            return ex.Message;
        }
    }

    private static async Task ThrowOnFailAsync(Task<Result<Unit>> step)
    {
        var result = await step.ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new IOException(result.Error);
    }

    private static string NotesPath(string root, StoryRef story) =>
        INotesService.DeriveNotesPath(root, story.RelativePath);
}
