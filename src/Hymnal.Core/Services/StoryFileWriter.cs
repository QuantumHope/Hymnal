using System.Collections.Concurrent;
using System.Text;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// The single path for writing a story <c>.md</c> file together with its trailing metadata block
/// (see <see cref="StoryFileFormat"/>); the editor save path uses <see cref="WriteFileAsync"/>.
/// <para>
/// When the file is held by the attached <see cref="IOpenStoryBuffer"/> (open in the editor), metadata changes
/// go to the buffer, which saves through the editor so unsaved prose is kept; the disk is not touched here.
/// Otherwise the file is read, split, given the new metadata, merged and written atomically, leaving the prose
/// bytes, a UTF-8 byte order mark and the line ending exactly as they were.
/// </para>
/// <para>
/// Metadata changes to the same file are serialized, so concurrent <see cref="UpdateMetadataAsync"/> calls
/// never lose an update. Files must be valid UTF-8; any other file fails rather than being rewritten.
/// </para>
/// </summary>
public sealed class StoryFileWriter(IMetadataStore store)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Two per-path locks: the metadata lock covers a whole read-modify-write (including a buffer apply, which
    // re-enters through WriteFileAsync); the file lock covers each disk read/write of the file.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(PathComparer);

    private volatile IOpenStoryBuffer? _buffer;

    /// <summary>Sets (or, with null, clears) the editor buffer that receives metadata changes for its file.</summary>
    public void AttachBuffer(IOpenStoryBuffer? buffer) => _buffer = buffer;

    /// <summary>
    /// Returns the file's metadata (the buffer's when the file is open), or null when it has none.
    /// A malformed block also yields null; use <see cref="ReadFileAsync"/> to tell the two apart.
    /// </summary>
    public async Task<Result<EmbeddedStoryMetadata?>> ReadMetadataAsync(string absolutePath)
    {
        var path = Path.GetFullPath(absolutePath);
        if (HeldBuffer(path) is { } buffer)
            return Result<EmbeddedStoryMetadata?>.Ok(buffer.CurrentMetadata);

        var split = await ReadFileAsync(path).ConfigureAwait(false);
        return split.IsSuccess
            ? Result<EmbeddedStoryMetadata?>.Ok(split.Value!.Metadata)
            : Result<EmbeddedStoryMetadata?>.Fail(split.Error!);
    }

    /// <summary>
    /// Reads and splits the file as it is on disk (ignoring any buffer), including
    /// <see cref="StorySplit.HadMalformedBlock"/>. A leading byte order mark stays in the prose.
    /// </summary>
    public async Task<Result<StorySplit>> ReadFileAsync(string absolutePath)
    {
        var path = Path.GetFullPath(absolutePath);
        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadSplitAsync(path).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the file's current metadata (null when it has none or its block is
    /// malformed) and stores the result. A malformed block is kept as prose and the new block is appended after it.
    /// </summary>
    public async Task<Result<Unit>> UpdateMetadataAsync(
        string absolutePath, Func<EmbeddedStoryMetadata?, EmbeddedStoryMetadata> update)
    {
        var path = Path.GetFullPath(absolutePath);
        var gate = Lock(_metadataLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HeldBuffer(path) is { } buffer)
                return await buffer.ApplyMetadataAsync(update(buffer.CurrentMetadata)).ConfigureAwait(false);

            return await RewriteClosedFileAsync(path, split => update(split.Metadata)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes <paramref name="prose"/> followed by the block for <paramref name="metadata"/> (none when null),
    /// using <paramref name="lineEnding"/> for the block. Always writes to disk; this is the editor save path.
    /// </summary>
    public async Task<Result<Unit>> WriteFileAsync(
        string absolutePath, string prose, EmbeddedStoryMetadata? metadata, string lineEnding)
    {
        var path = Path.GetFullPath(absolutePath);
        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await WriteAsync(path, StoryFileFormat.Merge(prose, metadata, lineEnding)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Strips the recognized block so the file holds exactly its prose again. Does nothing (and writes nothing)
    /// when there is no recognized block; a malformed block is prose and is kept.
    /// </summary>
    public async Task<Result<Unit>> RemoveMetadataAsync(string absolutePath)
    {
        var path = Path.GetFullPath(absolutePath);
        var gate = Lock(_metadataLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HeldBuffer(path) is { } buffer)
            {
                return buffer.CurrentMetadata is null
                    ? Result<Unit>.Ok(Unit.Default)
                    : await buffer.ApplyMetadataAsync(null).ConfigureAwait(false);
            }

            return await RewriteClosedFileAsync(path, _ => null).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads, splits and re-merges a file that is not open, with the metadata <paramref name="newMetadata"/>
    /// returns. Skips the write when the file has no recognized block and the new metadata is null.
    /// </summary>
    private async Task<Result<Unit>> RewriteClosedFileAsync(
        string path, Func<StorySplit, EmbeddedStoryMetadata?> newMetadata)
    {
        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = await ReadSplitAsync(path).ConfigureAwait(false);
            if (!read.IsSuccess)
                return Result<Unit>.Fail(read.Error!);

            var split = read.Value!;
            var metadata = newMetadata(split);
            if (metadata is null && split.Metadata is null)
                return Result<Unit>.Ok(Unit.Default);

            return await WriteAsync(path, StoryFileFormat.Merge(split.Prose, metadata, split.LineEnding))
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Result<StorySplit>> ReadSplitAsync(string path)
    {
        if (!File.Exists(path))
            return Result<StorySplit>.Fail($"Story file not found: {path}");

        try
        {
            // Decoding the bytes directly (not via a StreamReader) keeps a leading byte order mark as U+FEFF,
            // which the store writes back as the same three bytes.
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return Result<StorySplit>.Ok(StoryFileFormat.Split(StrictUtf8.GetString(bytes)));
        }
        catch (DecoderFallbackException)
        {
            return Result<StorySplit>.Fail($"Story file is not valid UTF-8 and was left unchanged: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<StorySplit>.Fail($"Could not read story file {path}: {ex.Message}");
        }
    }

    private async Task<Result<Unit>> WriteAsync(string path, string content)
    {
        try
        {
            await store.WriteTextAtomicAsync(path, content).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"Could not write story file {path}: {ex.Message}");
        }
    }

    private IOpenStoryBuffer? HeldBuffer(string path) =>
        _buffer is { } buffer && buffer.HoldsFile(path) ? buffer : null;

    private static SemaphoreSlim Lock(ConcurrentDictionary<string, SemaphoreSlim> locks, string path) =>
        locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
}
