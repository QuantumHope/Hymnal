using System.Text.Json;
using System.Text.RegularExpressions;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed partial class BeatBoardService(IMetadataStore store) : IBeatBoardService
{
    /// <summary>Board key for a Short Story project, which has exactly one story.</summary>
    public const string ShortStoryBoardKey = "story";

    private const int CurrentSchemaVersion = 1;

    /// <summary>Test seam: runs between MoveBoardAsync's checks and the move itself.</summary>
    internal Action? BeforeMove { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey)
    {
        if (!IsValidBoardKey(boardKey))
            return Result<IReadOnlyList<Beat>>.Fail("Beat board key is not valid.");

        var path = BoardFilePath(workspaceRoot, boardKey);
        if (!File.Exists(path))
            return Result<IReadOnlyList<Beat>>.Ok([]);

        var name = Path.GetFileName(path);
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<BoardFile>(json, JsonOptions);
            if (doc is null)
                return Result<IReadOnlyList<Beat>>.Fail($"{name} is empty or invalid.");
            if (doc.SchemaVersion != CurrentSchemaVersion)
                return Result<IReadOnlyList<Beat>>.Fail(
                    $"{name} has unsupported schemaVersion {doc.SchemaVersion}. Expected {CurrentSchemaVersion}.");
            return ValidateBeats(doc.Beats, name);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<Beat>>.Fail($"{name} could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<IReadOnlyList<Beat>>.Fail($"{name} could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<IReadOnlyList<Beat>>.Fail($"{name} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The load rules for stored beats: fails on an empty entry, a beat without an id or title, or a duplicate id;
    /// a missing note reads as "". <paramref name="sourceName"/> begins each error message.
    /// </summary>
    internal static Result<IReadOnlyList<Beat>> ValidateBeats(IEnumerable<Beat?>? beats, string sourceName)
    {
        var valid = new List<Beat>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in beats ?? [])
        {
            if (b is null)
                return Result<IReadOnlyList<Beat>>.Fail($"{sourceName} contains an empty beat entry.");
            if (string.IsNullOrWhiteSpace(b.Id) || b.Title is null)
                return Result<IReadOnlyList<Beat>>.Fail($"{sourceName} contains a beat without an id or title.");
            if (!seenIds.Add(b.Id))
                return Result<IReadOnlyList<Beat>>.Fail($"{sourceName} contains duplicate beat id '{b.Id}'.");
            valid.Add(b with { Note = b.Note ?? "" });
        }
        return Result<IReadOnlyList<Beat>>.Ok(valid);
    }

    public async Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats)
    {
        if (!IsValidBoardKey(boardKey))
            return Result<Unit>.Fail("Beat board key is not valid.");

        if (beats is null || beats.Any(b => b is null))
            return Result<Unit>.Fail("Beat board cannot be saved with missing beats.");

        var path = BoardFilePath(workspaceRoot, boardKey);
        try
        {
            var file = new BoardFile { SchemaVersion = CurrentSchemaVersion, Beats = beats.ToList() };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await store.WriteTextAtomicAsync(path, json).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"{Path.GetFileName(path)} could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"{Path.GetFileName(path)} could not be saved: {ex.Message}");
        }
    }

    public bool BoardExists(string workspaceRoot, string boardKey) =>
        IsValidBoardKey(boardKey) && File.Exists(BoardFilePath(workspaceRoot, boardKey));

    public Task<Result<Unit>> MoveBoardAsync(string workspaceRoot, string fromKey, string toKey)
    {
        if (!IsValidBoardKey(fromKey) || !IsValidBoardKey(toKey))
            return Task.FromResult(Result<Unit>.Fail("Beat board key is not valid."));
        if (string.Equals(fromKey, toKey, StringComparison.Ordinal))
            return Task.FromResult(Result<Unit>.Fail("A beat board can't be moved onto itself."));

        var source = BoardFilePath(workspaceRoot, fromKey);
        var destination = BoardFilePath(workspaceRoot, toKey);
        if (!File.Exists(source))
            return Task.FromResult(Result<Unit>.Fail($"{Path.GetFileName(source)} does not exist."));
        if (File.Exists(destination))
            return Task.FromResult(Result<Unit>.Fail($"{Path.GetFileName(destination)} already exists; it was not overwritten."));

        try
        {
            BeforeMove?.Invoke();
            // One rename in the same folder: atomic, and it refuses to replace a board created meanwhile.
            File.Move(source, destination, overwrite: false);
            return Task.FromResult(Result<Unit>.Ok(Unit.Default));
        }
        catch (IOException ex)
        {
            return Task.FromResult(Result<Unit>.Fail($"{Path.GetFileName(source)} could not be moved: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(Result<Unit>.Fail($"{Path.GetFileName(source)} could not be moved: {ex.Message}"));
        }
    }

    public Task<Result<Unit>> DeleteBoardAsync(string workspaceRoot, string boardKey)
    {
        if (!IsValidBoardKey(boardKey))
            return Task.FromResult(Result<Unit>.Fail("Beat board key is not valid."));

        var path = BoardFilePath(workspaceRoot, boardKey);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return Task.FromResult(Result<Unit>.Ok(Unit.Default));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result<Unit>.Fail($"{Path.GetFileName(path)} could not be deleted: {ex.Message}"));
        }
    }

    /// <exception cref="ArgumentException">The key is not a valid board key.</exception>
    public static string BoardFilePath(string workspaceRoot, string boardKey)
    {
        if (!IsValidBoardKey(boardKey))
            throw new ArgumentException("Beat board key is not valid.", nameof(boardKey));
        return Path.Combine(workspaceRoot, ".hymnal-data", "beats", boardKey + ".json");
    }

    /// <summary>A key must be a bare, platform-independent file name: ASCII letters, digits, '_' and '-' only.</summary>
    public static bool IsValidBoardKey(string? boardKey) =>
        boardKey is not null && BoardKeyPattern().IsMatch(boardKey);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex BoardKeyPattern();

    private sealed class BoardFile
    {
        public int SchemaVersion { get; set; }
        public List<Beat>? Beats { get; set; }
    }
}
