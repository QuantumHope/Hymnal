using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models.Runs;

namespace Hymnal.Core.Infrastructure;

/// <inheritdoc />
public sealed class WriterProfileStore : IWriterProfileStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IMetadataStore _store;
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WriterProfileStore(IMetadataStore store)
        : this(store, DefaultPath())
    {
    }

    public WriterProfileStore(IMetadataStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hymnal",
            "writer-profile.json");

    public async Task<Result<IReadOnlyList<RunRecord>>> LoadRunsAsync()
    {
        var loaded = await LoadDocumentAsync().ConfigureAwait(false);
        if (!loaded.IsSuccess)
            return Result<IReadOnlyList<RunRecord>>.Fail(loaded.Error!);
        return ReadSessions(loaded.Value!);
    }

    public async Task<Result<Unit>> AppendRunAsync(RunRecord session)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        IDisposable? fileLock = null;
        try
        {
            // The profile is global: another running Hymnal may be appending too, so the whole read-append-write
            // holds the cross-process lock, or one of the two runs would be lost.
            fileLock = await CrossProcessFileLock.AcquireAsync(_path).ConfigureAwait(false);
            var loaded = await LoadDocumentAsync().ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return Result<Unit>.Fail(loaded.Error!);

            // Work on the JSON tree so fields a newer Hymnal added are kept.
            var doc = loaded.Value!;
            if (doc["sessions"] is not JsonArray sessions)
            {
                sessions = new JsonArray();
                doc["sessions"] = sessions;
            }
            sessions.Add(JsonSerializer.SerializeToNode(session, JsonOptions));

            await _store.WriteTextAtomicAsync(_path, doc.ToJsonString(JsonOptions)).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"writer-profile.json could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"writer-profile.json could not be saved: {ex.Message}");
        }
        finally
        {
            fileLock?.Dispose();
            _lock.Release();
        }
    }

    private async Task<Result<JsonObject>> LoadDocumentAsync()
    {
        if (!File.Exists(_path))
            return Result<JsonObject>.Ok(new JsonObject { ["schemaVersion"] = CurrentSchemaVersion, ["sessions"] = new JsonArray() });

        try
        {
            var json = await File.ReadAllTextAsync(_path).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonObject doc)
                return Result<JsonObject>.Fail("writer-profile.json is not a JSON object.");

            var version = doc["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
            if (version != CurrentSchemaVersion)
                return Result<JsonObject>.Fail(
                    $"writer-profile.json has unsupported schemaVersion {version}. Expected {CurrentSchemaVersion}.");

            var sessions = ReadSessions(doc);
            return sessions.IsSuccess ? Result<JsonObject>.Ok(doc) : Result<JsonObject>.Fail(sessions.Error!);
        }
        catch (JsonException ex)
        {
            return Result<JsonObject>.Fail($"writer-profile.json could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<JsonObject>.Fail($"writer-profile.json could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<JsonObject>.Fail($"writer-profile.json could not be read: {ex.Message}");
        }
    }

    private static Result<IReadOnlyList<RunRecord>> ReadSessions(JsonObject doc)
    {
        try
        {
            var node = doc["sessions"];
            if (node is null)
                return Result<IReadOnlyList<RunRecord>>.Ok([]);
            if (node is not JsonArray)
                return Result<IReadOnlyList<RunRecord>>.Fail("writer-profile.json: 'sessions' is not a list.");

            var list = node.Deserialize<List<RunRecord?>>(JsonOptions) ?? [];
            if (list.Any(s => s is null || string.IsNullOrEmpty(s.LocalDate) || !DateOnly.TryParse(s.LocalDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                return Result<IReadOnlyList<RunRecord>>.Fail(
                    "writer-profile.json contains a session without a valid localDate.");
            return Result<IReadOnlyList<RunRecord>>.Ok(list!);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<RunRecord>>.Fail($"writer-profile.json could not be read: {ex.Message}");
        }
    }
}
