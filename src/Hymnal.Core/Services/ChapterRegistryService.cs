using System.Text.Json;
using System.Text.Json.Serialization;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class ChapterRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IMetadataStore _store;

    public ChapterRegistryService(IMetadataStore store)
    {
        _store = store;
    }

    // -------------------------------------------------------------------------
    // Load / Save
    // -------------------------------------------------------------------------

    public async Task<Dictionary<string, ChapterRegistryEntry>> LoadAsync(string workspaceRoot)
    {
        var path = RegistryPath(workspaceRoot);
        if (!File.Exists(path))
            return new Dictionary<string, ChapterRegistryEntry>();

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions)
                  ?? throw new InvalidDataException("chapter-registry.json deserialized to null.");

        if (doc.SchemaVersion != 1)
            throw new InvalidDataException(
                $"chapter-registry.json has unsupported schemaVersion {doc.SchemaVersion}. Expected 1.");

        return doc.Entries ?? new Dictionary<string, ChapterRegistryEntry>();
    }

    public async Task SaveAsync(string workspaceRoot, Dictionary<string, ChapterRegistryEntry> entries)
    {
        var file = new RegistryFile { SchemaVersion = 1, Entries = entries };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await _store.WriteTextAtomicAsync(RegistryPath(workspaceRoot), json).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Registry operations (pure / synchronous)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the existing UUID for <paramref name="relativePath"/> or assigns a new one.
    /// </summary>
    public (string uuid, bool wasNew) AssignUuid(
        Dictionary<string, ChapterRegistryEntry> registry,
        string relativePath,
        string? title = null)
    {
        // Search for an existing entry whose CurrentPath matches (case-insensitive on Windows)
        foreach (var (uuid, entry) in registry)
        {
            if (string.Equals(entry.CurrentPath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(entry.Title, title, StringComparison.Ordinal))
                {
                    registry[uuid] = new ChapterRegistryEntry
                    {
                        Uuid = entry.Uuid,
                        CurrentPath = entry.CurrentPath,
                        Orphaned = entry.Orphaned,
                        Title = title
                    };
                }
                return (uuid, false);
            }
        }

        var newUuid = Guid.NewGuid().ToString();
        registry[newUuid] = new ChapterRegistryEntry
        {
            Uuid = newUuid,
            CurrentPath = relativePath,
            Orphaned = false,
            Title = title
        };
        return (newUuid, true);
    }

    /// <summary>
    /// Updates CurrentPath for the entry whose CurrentPath matches <paramref name="oldPath"/>.
    /// Returns a new dictionary (entries are immutable records).
    /// </summary>
    public Dictionary<string, ChapterRegistryEntry> ReconcileRename(
        Dictionary<string, ChapterRegistryEntry> registry,
        string oldPath,
        string newPath)
    {
        var updated = new Dictionary<string, ChapterRegistryEntry>(registry.Count);
        foreach (var (uuid, entry) in registry)
        {
            updated[uuid] = string.Equals(entry.CurrentPath, oldPath, StringComparison.OrdinalIgnoreCase)
                ? new ChapterRegistryEntry { Uuid = entry.Uuid, CurrentPath = newPath, Orphaned = entry.Orphaned, Title = entry.Title }
                : entry;
        }
        return updated;
    }

    /// <summary>
    /// Marks entries Orphaned=true when their path is not in <paramref name="activePaths"/>;
    /// restores Orphaned=false when they reappear.
    /// </summary>
    public Dictionary<string, ChapterRegistryEntry> ReconcileOrphans(
        Dictionary<string, ChapterRegistryEntry> registry,
        IEnumerable<string> activePaths)
    {
        var activeSet = new HashSet<string>(
            activePaths, StringComparer.OrdinalIgnoreCase);

        var updated = new Dictionary<string, ChapterRegistryEntry>(registry.Count);
        foreach (var (uuid, entry) in registry)
        {
            var shouldBeOrphaned = !activeSet.Contains(entry.CurrentPath);
            updated[uuid] = shouldBeOrphaned != entry.Orphaned
                ? new ChapterRegistryEntry { Uuid = entry.Uuid, CurrentPath = entry.CurrentPath, Orphaned = shouldBeOrphaned, Title = entry.Title }
                : entry;
        }
        return updated;
    }

    /// <summary>
    /// Makes <paramref name="adoptedUuid"/> the UUID of the file at <paramref name="relativePath"/> (an embedded
    /// story's block id). An entry already under <paramref name="adoptedUuid"/> (e.g. orphaned when the file was
    /// renamed outside Hymnal) is moved to the path; the path's previous entry is dropped. The registry is
    /// returned unchanged when the path already has that UUID, or when <paramref name="adoptedUuid"/> belongs to
    /// another file in the book. Returns a new dictionary otherwise.
    /// </summary>
    public Dictionary<string, ChapterRegistryEntry> AdoptUuid(
        Dictionary<string, ChapterRegistryEntry> registry,
        string relativePath,
        string adoptedUuid)
    {
        if (string.IsNullOrWhiteSpace(adoptedUuid))
            return registry;

        registry.TryGetValue(adoptedUuid, out var claimed);
        if (claimed is { Orphaned: false })
        {
            // Already this file's UUID, or another file's: either way there is nothing to adopt.
            return registry;
        }

        var current = registry.Values.FirstOrDefault(entry =>
            string.Equals(entry.CurrentPath, relativePath, StringComparison.OrdinalIgnoreCase));
        var updated = registry
            .Where(kv => !string.Equals(kv.Value.CurrentPath, relativePath, StringComparison.OrdinalIgnoreCase)
                         && kv.Key != adoptedUuid)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        updated[adoptedUuid] = new ChapterRegistryEntry
        {
            Uuid = adoptedUuid,
            CurrentPath = relativePath,
            Orphaned = false,
            Title = current?.Title ?? claimed?.Title
        };
        return updated;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string RegistryPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "chapter-registry.json");

    // -------------------------------------------------------------------------
    // Internal DTO for serialization
    // -------------------------------------------------------------------------

    private sealed class RegistryFile
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, ChapterRegistryEntry>? Entries { get; set; }
    }
}
