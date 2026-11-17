using System.Text.Json;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models.Sprints;

namespace Hymnal.Core.Services;

/// <inheritdoc />
public sealed class FutureYouNotesService(IMetadataStore store) : IFutureYouNotesService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public static string NotesFilePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "future-you.json");

    public async Task<Result<IReadOnlyList<FutureYouNote>>> LoadAsync(string workspaceRoot)
    {
        var path = NotesFilePath(workspaceRoot);
        if (!File.Exists(path))
            return Result<IReadOnlyList<FutureYouNote>>.Ok([]);

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<NotesFile>(json, JsonOptions);
            if (doc is null)
                return Result<IReadOnlyList<FutureYouNote>>.Fail("future-you.json is empty or invalid.");
            if (doc.SchemaVersion != CurrentSchemaVersion)
                return Result<IReadOnlyList<FutureYouNote>>.Fail(
                    $"future-you.json has unsupported schemaVersion {doc.SchemaVersion}. Expected {CurrentSchemaVersion}.");
            var notes = doc.Notes ?? [];
            if (notes.Any(n => n is null || n.Text is null))
                return Result<IReadOnlyList<FutureYouNote>>.Fail("future-you.json contains a note without text.");
            return Result<IReadOnlyList<FutureYouNote>>.Ok(notes!);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<FutureYouNote>>.Fail($"future-you.json could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<IReadOnlyList<FutureYouNote>>.Fail($"future-you.json could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<IReadOnlyList<FutureYouNote>>.Fail($"future-you.json could not be read: {ex.Message}");
        }
    }

    public async Task<FutureYouNote?> LatestAsync(string workspaceRoot)
    {
        var loaded = await LoadAsync(workspaceRoot).ConfigureAwait(false);
        return loaded.IsSuccess && loaded.Value!.Count > 0
            ? loaded.Value!.MaxBy(n => n.CreatedAt)
            : null;
    }

    public async Task<Result<Unit>> AddAsync(string workspaceRoot, FutureYouNote note)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(workspaceRoot).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return Result<Unit>.Fail(loaded.Error!);

            var file = new NotesFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Notes = [.. loaded.Value!, note],
            };
            await store.WriteTextAtomicAsync(NotesFilePath(workspaceRoot), JsonSerializer.Serialize(file, JsonOptions))
                .ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"future-you.json could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"future-you.json could not be saved: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed class NotesFile
    {
        public int SchemaVersion { get; set; }
        public List<FutureYouNote?>? Notes { get; set; }
    }
}
