using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class BeatBoardServiceTests
{
    private static async Task WithTempDirAsync(Func<string, BeatBoardService, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-beats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir, new BeatBoardService(new MetadataStore()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void WriteRaw(string dir, string key, string content)
    {
        var path = BeatBoardService.BoardFilePath(dir, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public Task Load_missing_returns_empty() => WithTempDirAsync(async (dir, svc) =>
    {
        var result = await svc.LoadAsync(dir, BeatBoardService.ShortStoryBoardKey);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    });

    [Fact]
    public Task Save_then_load_round_trips_order_and_fields() => WithTempDirAsync(async (dir, svc) =>
    {
        var a = new Beat("b-1", "Hook", "Open strong.", "scenes/one.md");
        var b = new Beat("b-2", "Climax", "", null);
        var c = new Beat("b-3", "Resolution", "Wrap up.", null);

        Assert.True((await svc.SaveAsync(dir, "story", [b, a, c])).IsSuccess);
        var loaded = await svc.LoadAsync(dir, "story");

        Assert.True(loaded.IsSuccess);
        Assert.Equal([b, a, c], loaded.Value!);
    });

    [Fact]
    public Task Saved_json_shape() => WithTempDirAsync(async (dir, svc) =>
    {
        await svc.SaveAsync(dir, "story", [new Beat("b-1", "Hook", "n", "scenes/one.md")]);

        var json = File.ReadAllText(Path.Combine(dir, ".hymnal-data", "beats", "story.json"));
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"beats\"", json);
        Assert.Contains("\"scenePath\": \"scenes/one.md\"", json);
        Assert.Contains("\"id\": \"b-1\"", json);
    });

    [Fact]
    public Task Load_malformed_fails_and_leaves_file() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "story", "{ not json");

        var result = await svc.LoadAsync(dir, "story");

        Assert.False(result.IsSuccess);
        Assert.Equal("{ not json", File.ReadAllText(BeatBoardService.BoardFilePath(dir, "story")));
    });

    [Fact]
    public Task Load_unsupported_schema_fails() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "story", """{ "schemaVersion": 99, "beats": [] }""");

        var result = await svc.LoadAsync(dir, "story");

        Assert.False(result.IsSuccess);
        Assert.Contains("schemaVersion", result.Error);
    });

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "beats": [ null ] }""")]
    [InlineData("""{ "schemaVersion": 1, "beats": [ { "title": "T" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "beats": [ { "id": "  ", "title": "T" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "beats": [ { "id": "x" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "beats": [ { "id": "x", "title": "A" }, { "id": "x", "title": "B" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "beats": [ "x" ] }""")]
    public Task Load_null_entry_or_missing_id_or_title_fails(string json) => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "story", json);

        var result = await svc.LoadAsync(dir, "story");

        Assert.False(result.IsSuccess);
        Assert.Equal(json, File.ReadAllText(BeatBoardService.BoardFilePath(dir, "story")));
    });

    [Fact]
    public Task Missing_note_becomes_empty() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "story", """{ "schemaVersion": 1, "beats": [ { "id": "x", "title": "T" } ] }""");

        var result = await svc.LoadAsync(dir, "story");

        Assert.True(result.IsSuccess);
        Assert.Equal("", result.Value![0].Note);
        Assert.Null(result.Value[0].ScenePath);
        Assert.Null(result.Value[0].SceneId);
    });

    [Fact]
    public Task Scene_id_round_trips_as_sceneId() => WithTempDirAsync(async (dir, svc) =>
    {
        var uuid = Guid.NewGuid().ToString();
        var beat = new Beat("b-1", "Hook", "", "scenes/one.md", uuid);

        Assert.True((await svc.SaveAsync(dir, "story", [beat])).IsSuccess);

        Assert.Contains($"\"sceneId\": \"{uuid}\"", File.ReadAllText(BeatBoardService.BoardFilePath(dir, "story")));
        Assert.Equal(beat, Assert.Single((await svc.LoadAsync(dir, "story")).Value!));
    });

    [Fact]
    public Task Move_board_writes_destination_and_deletes_source() => WithTempDirAsync(async (dir, svc) =>
    {
        var uuid = Guid.NewGuid().ToString();
        var beat = new Beat("b-1", "Hook", "Open strong.", "one.md");
        await svc.SaveAsync(dir, "story", [beat]);
        var bytes = File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, "story"));

        var result = await svc.MoveBoardAsync(dir, "story", uuid);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(svc.BoardExists(dir, "story"));
        Assert.True(svc.BoardExists(dir, uuid));
        Assert.Equal(bytes, File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, uuid)));
        Assert.Equal(beat, Assert.Single((await svc.LoadAsync(dir, uuid)).Value!));
    });

    [Fact]
    public Task Move_board_onto_existing_destination_fails_and_changes_nothing() => WithTempDirAsync(async (dir, svc) =>
    {
        var uuid = Guid.NewGuid().ToString();
        await svc.SaveAsync(dir, "story", [new Beat("1", "Source", "", null)]);
        await svc.SaveAsync(dir, uuid, [new Beat("2", "Destination", "", null)]);
        var source = File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, "story"));
        var destination = File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, uuid));

        var result = await svc.MoveBoardAsync(dir, "story", uuid);

        Assert.False(result.IsSuccess);
        Assert.Equal(source, File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, "story")));
        Assert.Equal(destination, File.ReadAllBytes(BeatBoardService.BoardFilePath(dir, uuid)));
    });

    [Fact]
    public Task Move_board_with_missing_source_fails() => WithTempDirAsync(async (dir, svc) =>
    {
        var uuid = Guid.NewGuid().ToString();

        var result = await svc.MoveBoardAsync(dir, "story", uuid);

        Assert.False(result.IsSuccess);
        Assert.False(svc.BoardExists(dir, uuid));
        Assert.False((await svc.MoveBoardAsync(dir, "story", "story")).IsSuccess);
        Assert.False((await svc.MoveBoardAsync(dir, "../x", uuid)).IsSuccess);
        Assert.False(svc.BoardExists(dir, "../x"));
    });

    [Fact]
    public Task Boards_are_independent_per_key() => WithTempDirAsync(async (dir, svc) =>
    {
        var uuid = Guid.NewGuid().ToString();
        await svc.SaveAsync(dir, "story", [new Beat("1", "Story beat", "", null)]);
        await svc.SaveAsync(dir, uuid, [new Beat("2", "Anthology beat", "", null)]);

        var story = await svc.LoadAsync(dir, "story");
        var other = await svc.LoadAsync(dir, uuid);

        Assert.Equal("Story beat", Assert.Single(story.Value!).Title);
        Assert.Equal("Anthology beat", Assert.Single(other.Value!).Title);
    });

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    [InlineData("a:b")]
    [InlineData("a*")]
    [InlineData("story\n")]
    [InlineData("abc ")]
    [InlineData("abc.")]
    [InlineData("caf\u00e9")]
    public Task Key_with_path_characters_is_rejected(string key) => WithTempDirAsync(async (dir, svc) =>
    {
        Assert.False((await svc.LoadAsync(dir, key)).IsSuccess);
        Assert.False((await svc.SaveAsync(dir, key, [new Beat("1", "T", "", null)])).IsSuccess);

        Assert.False(Directory.Exists(Path.Combine(dir, ".hymnal-data")));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    });

    [Fact]
    public Task Null_beats_property_loads_empty() => WithTempDirAsync(async (dir, svc) =>
    {
        WriteRaw(dir, "story", """{ "schemaVersion": 1, "beats": null }""");

        var result = await svc.LoadAsync(dir, "story");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    });

    [Fact]
    public Task Save_null_beats_or_null_entry_fails_and_writes_nothing() => WithTempDirAsync(async (dir, svc) =>
    {
        Assert.False((await svc.SaveAsync(dir, "story", null!)).IsSuccess);
        Assert.False((await svc.SaveAsync(dir, "story", [new Beat("1", "T", "", null), null!])).IsSuccess);

        Assert.Empty(Directory.GetFileSystemEntries(dir));
    });

    [Fact]
    public void Registry_uuid_and_story_keys_are_valid()
    {
        Assert.True(BeatBoardService.IsValidBoardKey("story"));
        Assert.True(BeatBoardService.IsValidBoardKey(Guid.NewGuid().ToString()));
    }
}
