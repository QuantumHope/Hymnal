using System.Text.Json;
using System.Text.Json.Serialization;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class ProjectSettingsService(IMetadataStore store)
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<Result<ProjectSettings>> LoadAsync(string workspaceRoot)
    {
        var path = ProjectFilePath(workspaceRoot);
        if (!File.Exists(path))
            return Result<ProjectSettings>.Ok(ProjectSettings.Default);

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions);
            if (doc is null)
                return Result<ProjectSettings>.Fail("project.json is empty or invalid.");
            if (doc.SchemaVersion != CurrentSchemaVersion)
                return Result<ProjectSettings>.Fail(
                    $"project.json has unsupported schemaVersion {doc.SchemaVersion}. Expected {CurrentSchemaVersion}.");
            return Result<ProjectSettings>.Ok(new ProjectSettings(doc.Kind, doc.Storage));
        }
        catch (JsonException ex)
        {
            return Result<ProjectSettings>.Fail($"project.json could not be read: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result<ProjectSettings>.Fail($"project.json could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<ProjectSettings>.Fail($"project.json could not be read: {ex.Message}");
        }
    }

    public async Task<Result<Unit>> SaveAsync(string workspaceRoot, ProjectSettings settings)
    {
        try
        {
            var file = new ProjectFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Kind = settings.Kind,
                Storage = settings.Storage
            };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await store.WriteTextAtomicAsync(ProjectFilePath(workspaceRoot), json).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"project.json could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"project.json could not be saved: {ex.Message}");
        }
    }

    public static string ProjectFilePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".hymnal-data", "project.json");

    private sealed class ProjectFile
    {
        public int SchemaVersion { get; set; }
        public ProjectKind Kind { get; set; }
        public StorageMode Storage { get; set; }
    }
}
