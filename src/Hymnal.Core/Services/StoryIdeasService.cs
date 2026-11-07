using System.Text.Json;
using System.Text.Json.Serialization;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class StoryIdeasService(IMetadataStore store) : IStoryIdeasService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot)
    {
        var path = IdeasFilePath(workspaceRoot);
        if (!File.Exists(path))
            return Result<IReadOnlyList<StoryIdea>>.Ok([]);

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<IdeasFile>(json, JsonOptions);
            if (doc is null)
                return Result<IReadOnlyList<StoryIdea>>.Fail("ideas.json is empty or invalid.");
            if (doc.SchemaVersion != CurrentSchemaVersion)
                return Result<IReadOnlyList<StoryIdea>>.Fail(
                    $"ideas.json has unsupported schemaVersion {doc.SchemaVersion}. Expected {CurrentSchemaVersion}.");
            var ideas = new List<StoryIdea>();
            foreach (var i in doc.Ideas ?? [])
            {
                if (i is null)
                    return Result<IReadOnlyList<StoryIdea>>.Fail("ideas.json contains an empty idea entry.");
                if (string.IsNullOrWhiteSpace(i.Id) || i.Title is null)
                    return Result<IReadOnlyList<StoryIdea>>.Fail("ideas.json contains an idea without an id or title.");
                ideas.Add(i with { Premise = i.Premise ?? "", Notes = i.Notes ?? "", Tags = i.Tags ?? [] });
            }
            return Result<IReadOnlyList<StoryIdea>>.Ok(ideas);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<StoryIdea>>.Fail($"ideas.json could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<IReadOnlyList<StoryIdea>>.Fail($"ideas.json could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<IReadOnlyList<StoryIdea>>.Fail($"ideas.json could not be read: {ex.Message}");
        }
    }

    public async Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas)
    {
        try
        {
            var file = new IdeasFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Ideas = ideas.Select(i => i with { Tags = NormalizeTags(i.Tags ?? []) }).ToList()
            };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await store.WriteTextAtomicAsync(IdeasFilePath(workspaceRoot), json).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"ideas.json could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"ideas.json could not be saved: {ex.Message}");
        }
    }

    public static string IdeasFilePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "ideas.json");

    /// <summary>Trims, drops empties and de-duplicates case-insensitively, keeping the first spelling.</summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class IdeasFile
    {
        public int SchemaVersion { get; set; }
        public List<StoryIdea>? Ideas { get; set; }
    }
}
