using System.Text.Json;
using System.Text.Json.Serialization;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class PhaseDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// In-process mutex that serialises concurrent <see cref="UpsertAsync"/> /
    /// <see cref="SaveAsync"/> calls so that only one operation holds the file lock at a time.
    /// Without this, multiple rapid saves (e.g. CalendarDatePicker binding initialisation firing
    /// SelectedDateChanged for several rows simultaneously) would throw IOException because the
    /// OS-level file lock is not re-entrant.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly IMetadataStore _store;

    public PhaseDataService(IMetadataStore store)
    {
        _store = store;
    }

    // -------------------------------------------------------------------------
    // Defaults
    // -------------------------------------------------------------------------

    public static PhaseData DefaultPhaseData => new() { Status = ChapterStatus.Outlining };

    public static PhaseData DefaultPhaseDataFor(ProjectProfile profile) => new() { Status = profile.DefaultStatus };

    // -------------------------------------------------------------------------
    // Load / Save
    // -------------------------------------------------------------------------

    public async Task<Dictionary<string, PhaseData>> LoadAsync(string workspaceRoot)
    {
        var path = PhasesPath(workspaceRoot);
        if (!File.Exists(path))
            return new Dictionary<string, PhaseData>();

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize<PhasesFile>(json, JsonOptions)
                  ?? throw new InvalidDataException("phases.json deserialized to null.");

        if (doc.SchemaVersion != 1)
            throw new InvalidDataException(
                $"phases.json has unsupported schemaVersion {doc.SchemaVersion}. Expected 1.");

        return doc.Phases ?? new Dictionary<string, PhaseData>();
    }

    public async Task SaveAsync(string workspaceRoot, Dictionary<string, PhaseData> phases)
    {
        await WithWorkspaceLockAsync(workspaceRoot, async () =>
        {
            await SaveUnlockedAsync(workspaceRoot, phases).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task UpsertAsync(string workspaceRoot, string uuid, Func<PhaseData?, PhaseData> update)
    {
        await WithWorkspaceLockAsync(workspaceRoot, async () =>
        {
            var phases = await LoadUnlockedAsync(workspaceRoot).ConfigureAwait(false);
            phases[uuid] = update(phases.TryGetValue(uuid, out var current) ? current : null);
            await SaveUnlockedAsync(workspaceRoot, phases).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Removes <paramref name="uuid"/>'s entry; writes nothing when it has none.</summary>
    public async Task RemoveAsync(string workspaceRoot, string uuid)
    {
        await WithWorkspaceLockAsync(workspaceRoot, async () =>
        {
            var phases = await LoadUnlockedAsync(workspaceRoot).ConfigureAwait(false);
            if (phases.Remove(uuid))
                await SaveUnlockedAsync(workspaceRoot, phases).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, PhaseData>> LoadUnlockedAsync(string workspaceRoot)
    {
        var path = PhasesPath(workspaceRoot);
        if (!File.Exists(path))
            return new Dictionary<string, PhaseData>();

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize<PhasesFile>(json, JsonOptions)
                  ?? throw new InvalidDataException("phases.json deserialized to null.");

        if (doc.SchemaVersion != 1)
            throw new InvalidDataException(
                $"phases.json has unsupported schemaVersion {doc.SchemaVersion}. Expected 1.");

        return doc.Phases ?? new Dictionary<string, PhaseData>();
    }

    private async Task SaveUnlockedAsync(string workspaceRoot, Dictionary<string, PhaseData> phases)
    {
        var file = new PhasesFile { SchemaVersion = 1, Phases = phases };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await _store.WriteTextAtomicAsync(PhasesPath(workspaceRoot), json).ConfigureAwait(false);
    }

    private async Task WithWorkspaceLockAsync(string workspaceRoot, Func<Task> action)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var lockPath = PhasesLockPath(workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            using var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await action().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string PhasesPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "phases.json");

    private static string PhasesLockPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "phases.lock");

    // -------------------------------------------------------------------------
    // Internal DTO for serialization
    // -------------------------------------------------------------------------

    private sealed class PhasesFile
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, PhaseData>? Phases { get; set; }
    }
}
