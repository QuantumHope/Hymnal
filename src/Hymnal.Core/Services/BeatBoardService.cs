using System.Text.Json;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class BeatBoardService(IMetadataStore store) : IBeatBoardService
{
    /// <summary>Board key for a Short Story project, which has exactly one story.</summary>
    public const string ShortStoryBoardKey = "story";

    private const int CurrentSchemaVersion = 1;

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
            var beats = new List<Beat>();
            foreach (var b in doc.Beats ?? [])
            {
                if (b is null)
                    return Result<IReadOnlyList<Beat>>.Fail($"{name} contains an empty beat entry.");
                if (string.IsNullOrWhiteSpace(b.Id) || b.Title is null)
                    return Result<IReadOnlyList<Beat>>.Fail($"{name} contains a beat without an id or title.");
                beats.Add(b with { Note = b.Note ?? "" });
            }
            return Result<IReadOnlyList<Beat>>.Ok(beats);
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

    public async Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats)
    {
        if (!IsValidBoardKey(boardKey))
            return Result<Unit>.Fail("Beat board key is not valid.");

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

    /// <exception cref="ArgumentException">The key is not a valid board key.</exception>
    public static string BoardFilePath(string workspaceRoot, string boardKey)
    {
        if (!IsValidBoardKey(boardKey))
            throw new ArgumentException("Beat board key is not valid.", nameof(boardKey));
        return Path.Combine(workspaceRoot, ".hymnal-data", "beats", boardKey + ".json");
    }

    /// <summary>A key must be usable as a bare file name: no separators, no "..", no invalid filename characters.</summary>
    public static bool IsValidBoardKey(string? boardKey) =>
        !string.IsNullOrWhiteSpace(boardKey)
        && boardKey != "."
        && !boardKey.Contains("..", StringComparison.Ordinal)
        && boardKey.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && boardKey.IndexOfAny(['/', '\\']) < 0;

    private sealed class BoardFile
    {
        public int SchemaVersion { get; set; }
        public List<Beat>? Beats { get; set; }
    }
}
