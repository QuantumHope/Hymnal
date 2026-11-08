using System.Collections.Concurrent;
using System.Security;
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
/// bytes, a UTF-8 byte order mark and the line ending exactly as they were. A write that would not change the
/// file is skipped.
/// </para>
/// <para>
/// Paths are made absolute and symbolic links are resolved to their final target, which is the file read and
/// written (the link stays a link). Metadata changes to the same real file are serialized whatever spelling of
/// the path is used, so concurrent <see cref="UpdateMetadataAsync"/> calls never lose an update. Invalid paths,
/// missing or read-only files and files that are not valid UTF-8 fail without writing; no public method throws
/// for them.
/// </para>
/// </summary>
public sealed class StoryFileWriter(IMetadataStore store)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>How story paths are compared: case-insensitively on Windows, ordinally elsewhere.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Two per-path locks keyed by the real (link-resolved) full path: the metadata lock covers a whole
    // read-modify-write (including a buffer apply, which re-enters through WriteFileAsync); the file lock covers
    // each disk read/write of the file.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataLocks = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(PathComparer);

    private volatile IOpenStoryBuffer? _buffer;

    /// <summary>A story path made absolute (<see cref="Full"/>) and with symbolic links resolved (<see cref="Real"/>).</summary>
    private readonly record struct StoryPath(string Full, string Real);

    /// <summary>Sets (or, with null, clears) the editor buffer that receives metadata changes for its file.</summary>
    public void AttachBuffer(IOpenStoryBuffer? buffer) => _buffer = buffer;

    /// <summary>
    /// Waits for any metadata change in progress for the given files and holds further ones off until the
    /// returned handle is disposed. The editor holds it while it loads a file into (or switches its buffer
    /// away from) a story file, so every change either reaches the disk before the load or reaches the buffer
    /// after it. Null, empty and invalid paths are ignored; spellings of one real file take its lock once.
    /// The holder must not wait for a metadata change to the same files while holding it, or it deadlocks.
    /// </summary>
    public async Task<IDisposable> HoldMetadataChangesAsync(params string?[] absolutePaths)
    {
        var realPaths = absolutePaths
            .Select(p => TryResolve(p ?? "", out var path, out _) ? path.Real : null)
            .OfType<string>()
            .Distinct(PathComparer)
            .Order(PathComparer) // one global order, so two holders can never deadlock each other
            .ToList();

        var held = new List<SemaphoreSlim>(realPaths.Count);
        try
        {
            foreach (var real in realPaths)
            {
                var gate = _metadataLocks.GetOrAdd(real, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync().ConfigureAwait(false);
                held.Add(gate);
            }
        }
        catch
        {
            Release(held);
            throw;
        }

        return new Releaser(held);
    }

    /// <summary>
    /// Returns the file's metadata (the buffer's when the file is open), or null when it has none.
    /// A malformed block also yields null; use <see cref="ReadFileAsync"/> to tell the two apart.
    /// </summary>
    public async Task<Result<EmbeddedStoryMetadata?>> ReadMetadataAsync(string absolutePath)
    {
        if (!TryResolve(absolutePath, out var path, out var error))
            return Result<EmbeddedStoryMetadata?>.Fail(error);

        if (HeldBuffer(path) is { } buffer)
            return Result<EmbeddedStoryMetadata?>.Ok(buffer.CurrentMetadata);

        var (read, _) = await ReadLockedAsync(path).ConfigureAwait(false);
        return read.IsSuccess
            ? Result<EmbeddedStoryMetadata?>.Ok(read.Value.Split.Metadata)
            : Result<EmbeddedStoryMetadata?>.Fail(read.Error!);
    }

    /// <summary>
    /// Reads and splits the file as it is on disk (ignoring any buffer), including
    /// <see cref="StorySplit.HadMalformedBlock"/>. A leading byte order mark stays in the prose.
    /// </summary>
    public async Task<Result<StorySplit>> ReadFileAsync(string absolutePath) =>
        (await TryReadFileAsync(absolutePath).ConfigureAwait(false)).Result;

    /// <summary>
    /// Like <see cref="ReadFileAsync"/>, and on failure also says whether it failed only because the file is not
    /// valid UTF-8 (<see cref="StoryFileRead.NotUtf8"/>), so the editor can still open it as plain text.
    /// </summary>
    public async Task<StoryFileRead> TryReadFileAsync(string absolutePath)
    {
        if (!TryResolve(absolutePath, out var path, out var error))
            return new StoryFileRead(Result<StorySplit>.Fail(error), NotUtf8: false);

        var (read, notUtf8) = await ReadLockedAsync(path).ConfigureAwait(false);
        return new StoryFileRead(
            read.IsSuccess ? Result<StorySplit>.Ok(read.Value.Split) : Result<StorySplit>.Fail(read.Error!),
            notUtf8);
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the file's current metadata (null when it has none or its block is
    /// malformed) and stores the result. A malformed block is kept as prose and the new block is appended after it.
    /// <paramref name="update"/> runs while this file's locks are held: it must be pure (no I/O) and must not call
    /// back into this writer, or it deadlocks.
    /// </summary>
    public async Task<Result<Unit>> UpdateMetadataAsync(
        string absolutePath, Func<EmbeddedStoryMetadata?, EmbeddedStoryMetadata> update)
    {
        if (!TryResolve(absolutePath, out var path, out var error))
            return Result<Unit>.Fail(error);

        var gate = Lock(_metadataLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HeldBuffer(path) is { } buffer)
            {
                var applied = await buffer.ApplyMetadataAsync(update(buffer.CurrentMetadata)).ConfigureAwait(false);
                if (applied.IsSuccess || HeldBuffer(path) is not null)
                    return applied;
                // The buffer let go of the file before the change reached it (closed in the editor):
                // the file is closed now, so write it as one.
            }

            return await RewriteClosedFileAsync(path, split => (split.Prose, update(split.Metadata))).ConfigureAwait(false);
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
        if (!TryResolve(absolutePath, out var path, out var error))
            return Result<Unit>.Fail(error);

        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await WriteAsync(path.Real, StoryFileFormat.Merge(prose, metadata, lineEnding)).ConfigureAwait(false);
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
        if (!TryResolve(absolutePath, out var path, out var error))
            return Result<Unit>.Fail(error);

        var gate = Lock(_metadataLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HeldBuffer(path) is { } buffer)
            {
                if (buffer.CurrentMetadata is null)
                    return Result<Unit>.Ok(Unit.Default);

                var applied = await buffer.ApplyMetadataAsync(null).ConfigureAwait(false);
                if (applied.IsSuccess || HeldBuffer(path) is not null)
                    return applied;
                // As in UpdateMetadataAsync: the buffer closed the file meanwhile, so rewrite it as a closed file.
            }

            return await RewriteClosedFileAsync(path, split => (split.Prose, null)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the prose of a file that is not open in the editor, keeping its
    /// block, line ending and a UTF-8 byte order mark (which stays at the start of the prose passed to
    /// <paramref name="change"/>). A malformed block is part of the prose. Fails without writing when the file is
    /// open in the editor (its buffer owns the prose), and like the other writes for missing, read-only and
    /// non-UTF-8 files. <paramref name="change"/> runs while this file's locks are held: it must be pure.
    /// </summary>
    public async Task<Result<Unit>> RewriteProseAsync(string absolutePath, Func<string, string> change)
    {
        if (!TryResolve(absolutePath, out var path, out var error))
            return Result<Unit>.Fail(error);

        var gate = Lock(_metadataLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HeldBuffer(path) is not null)
                return Result<Unit>.Fail($"Story file is open in the editor and was left unchanged: {path.Full}");

            return await RewriteClosedFileAsync(path, split => (change(split.Prose), split.Metadata))
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads, splits and re-merges a file that is not open, with the prose and metadata <paramref name="rewrite"/>
    /// returns. Skips the write when the merged text equals the file's current text.
    /// </summary>
    private async Task<Result<Unit>> RewriteClosedFileAsync(
        StoryPath path, Func<StorySplit, (string Prose, EmbeddedStoryMetadata? Metadata)> rewrite)
    {
        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (read, _) = await ReadAsync(path.Real).ConfigureAwait(false);
            if (!read.IsSuccess)
                return Result<Unit>.Fail(read.Error!);

            var (original, split) = read.Value;
            var (prose, metadata) = rewrite(split);
            var merged = StoryFileFormat.Merge(prose, metadata, split.LineEnding);
            if (string.Equals(merged, original, StringComparison.Ordinal))
                return Result<Unit>.Ok(Unit.Default);

            return await WriteAsync(path.Real, merged).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(Result<(string Text, StorySplit Split)> Read, bool NotUtf8)> ReadLockedAsync(StoryPath path)
    {
        var gate = Lock(_fileLocks, path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadAsync(path.Real).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads and splits <paramref name="path"/>; <c>NotUtf8</c> is true when it failed only on decoding.</summary>
    private static async Task<(Result<(string Text, StorySplit Split)> Read, bool NotUtf8)> ReadAsync(string path)
    {
        if (!File.Exists(path))
            return (Result<(string, StorySplit)>.Fail($"Story file not found: {path}"), false);

        try
        {
            // Decoding the bytes directly (not via a StreamReader) keeps a leading byte order mark as U+FEFF,
            // which the store writes back as the same three bytes.
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var text = StrictUtf8.GetString(bytes);
            return (Result<(string, StorySplit)>.Ok((text, StoryFileFormat.Split(text))), false);
        }
        catch (DecoderFallbackException)
        {
            return (Result<(string, StorySplit)>.Fail($"Story file is not valid UTF-8 and was left unchanged: {path}"), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Result<(string, StorySplit)>.Fail($"Could not read story file {path}: {ex.Message}"), false);
        }
    }

    private async Task<Result<Unit>> WriteAsync(string path, string content)
    {
        try
        {
            // The atomic write replaces the file by rename, which would silently bypass a read-only file.
            if (IsReadOnly(path))
                return Result<Unit>.Fail($"Story file is read-only and was left unchanged: {path}");

            await store.WriteTextAtomicAsync(path, content).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"Could not write story file {path}: {ex.Message}");
        }
    }

    private static bool IsReadOnly(string path)
    {
        if (!File.Exists(path))
            return false;

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            return true;

        return !OperatingSystem.IsWindows() && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Makes <paramref name="absolutePath"/> absolute (<paramref name="fullPath"/>) and resolves a symbolic link
    /// to its final target (<paramref name="realPath"/>; the full path when it is not a link), exactly as the
    /// writer does. Returns false for empty or invalid paths and unresolvable links.
    /// </summary>
    public static bool TryResolvePath(string absolutePath, out string fullPath, out string realPath)
    {
        var ok = TryResolve(absolutePath, out var path, out _);
        fullPath = path.Full;
        realPath = path.Real;
        return ok;
    }

    /// <summary>
    /// Makes <paramref name="absolutePath"/> absolute and resolves a symbolic link to its final target.
    /// Returns false with an error for invalid paths or unresolvable links.
    /// </summary>
    private static bool TryResolve(string absolutePath, out StoryPath path, out string error)
    {
        path = default;
        error = "";
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            error = "Story file path is empty.";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(absolutePath);
            var file = new FileInfo(full);
            var real = file.LinkTarget is null ? full : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
            path = new StoryPath(full, real);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
                                       or UnauthorizedAccessException or SecurityException)
        {
            error = $"Invalid story file path '{absolutePath}': {ex.Message}";
            return false;
        }
    }

    private static void Release(List<SemaphoreSlim> gates)
    {
        for (var i = gates.Count - 1; i >= 0; i--)
            gates[i].Release();
    }

    /// <summary>Releases the locks taken by <see cref="HoldMetadataChangesAsync"/> once.</summary>
    private sealed class Releaser(List<SemaphoreSlim> gates) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release(gates);
        }
    }

    /// <summary>The buffer when it holds the file under either its given full path or its real path.</summary>
    private IOpenStoryBuffer? HeldBuffer(StoryPath path) =>
        _buffer is { } buffer
        && (buffer.HoldsFile(path.Full) || (!PathComparer.Equals(path.Full, path.Real) && buffer.HoldsFile(path.Real)))
            ? buffer
            : null;

    private static SemaphoreSlim Lock(ConcurrentDictionary<string, SemaphoreSlim> locks, StoryPath path) =>
        locks.GetOrAdd(path.Real, _ => new SemaphoreSlim(1, 1));
}
