using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models.Runs;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services.Runs;

public class WriterProfileStoreTests
{
    private static async Task WithTempDirAsync(Func<string, WriterProfileStore, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir, new WriterProfileStore(new MetadataStore(), Path.Combine(dir, "writer-profile.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static RunRecord Session(string date, int added = 100) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StartedAt = DateTimeOffset.Parse(date + "T20:00:00+00:00"),
        LocalDate = date,
        PlannedSeconds = 900,
        ActualSeconds = 900,
        Completed = true,
        Workspace = "Novel",
        WordsAdded = added,
        WordsCut = 10,
        WordsNet = added - 10,
        Chapters = 1,
    };

    [Fact]
    public Task Missing_file_is_an_empty_profile() => WithTempDirAsync(async (_, store) =>
    {
        var loaded = await store.LoadRunsAsync();
        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value!);
    });

    [Fact]
    public Task Appended_sessions_round_trip() => WithTempDirAsync(async (_, store) =>
    {
        var a = Session("2026-11-16");
        var b = Session("2026-11-17", added: 512);
        Assert.True((await store.AppendRunAsync(a)).IsSuccess);
        Assert.True((await store.AppendRunAsync(b)).IsSuccess);

        var loaded = await store.LoadRunsAsync();
        Assert.Equal([a, b], loaded.Value!);
    });

    [Fact]
    public Task Unknown_fields_from_a_newer_Hymnal_are_kept() => WithTempDirAsync(async (dir, store) =>
    {
        var path = Path.Combine(dir, "writer-profile.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "sessions": [], "unlocks": ["neon-palette"] }""");

        Assert.True((await store.AppendRunAsync(Session("2026-11-17"))).IsSuccess);
        Assert.Contains("neon-palette", File.ReadAllText(path));
    });

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "schemaVersion": 2, "sessions": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "sessions": [ { "id": "x" } ] }""")]
    public Task Malformed_files_are_reported_and_never_overwritten(string content) => WithTempDirAsync(async (dir, store) =>
    {
        var path = Path.Combine(dir, "writer-profile.json");
        File.WriteAllText(path, content);

        Assert.False((await store.LoadRunsAsync()).IsSuccess);
        Assert.False((await store.AppendRunAsync(Session("2026-11-17"))).IsSuccess);
        Assert.Equal(content, File.ReadAllText(path));
    });
}

public class FutureYouNotesServiceTests
{
    private static async Task WithTempDirAsync(Func<string, FutureYouNotesService, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-futureyou-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir, new FutureYouNotesService(new MetadataStore()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public Task No_file_means_no_latest_note() => WithTempDirAsync(async (dir, svc) =>
    {
        Assert.Null(await svc.LatestAsync(dir));
        Assert.Empty((await svc.LoadAsync(dir)).Value!);
    });

    [Fact]
    public Task Notes_are_appended_and_the_newest_is_latest() => WithTempDirAsync(async (dir, svc) =>
    {
        var older = new FutureYouNote { CreatedAt = DateTimeOffset.Parse("2026-11-16T20:00:00Z"), Text = "Finish the duel." };
        var newer = new FutureYouNote
        {
            CreatedAt = DateTimeOffset.Parse("2026-11-17T20:00:00Z"),
            Text = "Mara finds the letter.",
            ChapterPath = "ch2.md",
            ChapterTitle = "The Letter",
            LastLine = "She closed the door.",
        };
        Assert.True((await svc.AddAsync(dir, older)).IsSuccess);
        Assert.True((await svc.AddAsync(dir, newer)).IsSuccess);

        Assert.Equal([older, newer], (await svc.LoadAsync(dir)).Value!);
        Assert.Equal(newer, await svc.LatestAsync(dir));
    });

    [Fact]
    public Task A_malformed_file_is_never_overwritten() => WithTempDirAsync(async (dir, svc) =>
    {
        var path = FutureYouNotesService.NotesFilePath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken");

        var result = await svc.AddAsync(dir, new FutureYouNote { Text = "x" });
        Assert.False(result.IsSuccess);
        Assert.Equal("{ broken", File.ReadAllText(path));
        Assert.Null(await svc.LatestAsync(dir));
    });
}
