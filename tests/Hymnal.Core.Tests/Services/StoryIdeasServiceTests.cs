using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class StoryIdeasServiceTests
{
    private static async Task WithTempDirAsync(Func<string, StoryIdeasService, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-ideas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir, new StoryIdeasService(new MetadataStore()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void WriteRaw(string dir, string content)
    {
        var path = StoryIdeasService.IdeasFilePath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public Task Load_missing_file_returns_empty() => WithTempDirAsync(async (dir, svc) =>
    {
        var result = await svc.LoadAsync(dir);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    });

    [Fact]
    public Task Save_then_load_round_trips_all_fields() => WithTempDirAsync(async (dir, svc) =>
    {
        var first = new StoryIdea("id-1", "Ghost Ship", "A ship returns.", "Check tides.", ["horror"], "flash",
            IdeaState.Used, new DateOnly(2026, 11, 7), "abc");
        var second = StoryIdea.Create("Second", "Premise two", new DateOnly(2026, 11, 8));

        var save = await svc.SaveAsync(dir, [first, second]);
        Assert.True(save.IsSuccess);
        var loaded = await svc.LoadAsync(dir);

        Assert.True(loaded.IsSuccess);
        var ideas = loaded.Value!;
        Assert.Equal(2, ideas.Count);
        AssertSame(first, ideas[0]);
        AssertSame(second, ideas[1]);
    });

    private static void AssertSame(StoryIdea expected, StoryIdea actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Premise, actual.Premise);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.TargetPreset, actual.TargetPreset);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.StoryId, actual.StoryId);
    }

    [Fact]
    public Task Saved_json_uses_camelcase_strings() => WithTempDirAsync(async (dir, svc) =>
    {
        var idea = new StoryIdea("id-1", "T", "P", "", [], "flash", IdeaState.Used, new DateOnly(2026, 11, 7), null);
        await svc.SaveAsync(dir, [idea]);
        var json = File.ReadAllText(StoryIdeasService.IdeasFilePath(dir));
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"state\": \"used\"", json);
        Assert.Contains("\"targetPreset\": \"flash\"", json);
        Assert.Contains("\"createdAt\": \"2026-11-07\"", json);
    });

    [Fact]
    public Task Load_malformed_json_fails_and_leaves_file() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "{ not json");
        var before = File.ReadAllBytes(StoryIdeasService.IdeasFilePath(dir));
        var result = await svc.LoadAsync(dir);
        Assert.False(result.IsSuccess);
        Assert.Equal(before, File.ReadAllBytes(StoryIdeasService.IdeasFilePath(dir)));
    });

    [Fact]
    public Task Load_unsupported_schema_fails() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "{\"schemaVersion\":2,\"ideas\":[]}");
        var result = await svc.LoadAsync(dir);
        Assert.False(result.IsSuccess);
    });

    [Fact]
    public Task Load_unknown_state_fails() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "{\"schemaVersion\":1,\"ideas\":[{\"id\":\"a\",\"title\":\"T\",\"premise\":\"\",\"notes\":\"\"," +
                      "\"tags\":[],\"state\":\"archived\",\"createdAt\":\"2026-11-07\"}]}");
        var result = await svc.LoadAsync(dir);
        Assert.False(result.IsSuccess);
    });

    [Fact]
    public Task Load_null_tags_become_empty() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "{\"schemaVersion\":1,\"ideas\":[{\"id\":\"a\",\"title\":\"T\",\"premise\":\"\",\"notes\":\"\"," +
                      "\"state\":\"new\",\"createdAt\":\"2026-11-07\"}]}");
        var result = await svc.LoadAsync(dir);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value![0].Tags);
        Assert.Empty(result.Value[0].Tags);
    });

    [Fact]
    public void NormalizeTags_trims_dedupes_case_insensitively_keeps_first_spelling()
    {
        Assert.Equal(["Horror", "SF"], StoryIdeasService.NormalizeTags([" Horror", "horror", "", "SF "]));
    }

    [Fact]
    public Task Save_normalizes_tags_before_writing() => WithTempDirAsync(async (dir, svc) =>
    {
        var idea = StoryIdea.Create("T", "P", new DateOnly(2026, 11, 7)) with { Tags = [" Horror", "horror", "", "SF "] };
        await svc.SaveAsync(dir, [idea]);
        var loaded = await svc.LoadAsync(dir);
        Assert.Equal(["Horror", "SF"], loaded.Value![0].Tags);
    });
}
