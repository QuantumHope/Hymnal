using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class ProjectSettingsServiceTests
{
    private static async Task WithTempDirAsync(Func<string, ProjectSettingsService, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-ps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir, new ProjectSettingsService(new MetadataStore()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public Task Load_missing_returns_default() => WithTempDirAsync(async (dir, svc) =>
    {
        var result = await svc.LoadAsync(dir);
        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectSettings.Default, result.Value);
    });

    [Theory]
    [InlineData(ProjectKind.Novel, StorageMode.Sidecar)]
    [InlineData(ProjectKind.Novel, StorageMode.Embedded)]
    [InlineData(ProjectKind.Story, StorageMode.Sidecar)]
    [InlineData(ProjectKind.Story, StorageMode.Embedded)]
    [InlineData(ProjectKind.Anthology, StorageMode.Sidecar)]
    [InlineData(ProjectKind.Anthology, StorageMode.Embedded)]
    public Task Save_then_load_round_trips(ProjectKind kind, StorageMode storage) => WithTempDirAsync(async (dir, svc) =>
    {
        var settings = new ProjectSettings(kind, storage);
        var save = await svc.SaveAsync(dir, settings);
        Assert.True(save.IsSuccess);
        var loaded = await svc.LoadAsync(dir);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(settings, loaded.Value);
    });

    [Fact]
    public Task Save_writes_camelcase_schema_v1() => WithTempDirAsync(async (dir, svc) =>
    {
        await svc.SaveAsync(dir, new ProjectSettings(ProjectKind.Anthology, StorageMode.Sidecar));
        var text = await File.ReadAllTextAsync(ProjectSettingsService.ProjectFilePath(dir));
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"kind\": \"anthology\"", text);
        Assert.Contains("\"storage\": \"sidecar\"", text);
    });

    [Fact]
    public Task Load_unknown_kind_returns_fail() => WithTempDirAsync(async (dir, svc) =>
    {
        var path = ProjectSettingsService.ProjectFilePath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"kind\":\"poem\",\"storage\":\"sidecar\"}");
        var result = await svc.LoadAsync(dir);
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrEmpty(result.Error));
    });

    [Fact]
    public Task Load_malformed_returns_fail_and_does_not_touch_file() => WithTempDirAsync(async (dir, svc) =>
    {
        var path = ProjectSettingsService.ProjectFilePath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{not json");
        var before = await File.ReadAllBytesAsync(path);
        var result = await svc.LoadAsync(dir);
        Assert.False(result.IsSuccess);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    });
}
