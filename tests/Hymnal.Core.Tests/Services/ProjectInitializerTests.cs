using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class ProjectInitializerTests
{
    private static async Task WithTempDirAsync(Func<string, ProjectInitializer, ProjectSettingsService, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-pi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new MetadataStore();
            var settings = new ProjectSettingsService(store);
            await body(dir, new ProjectInitializer(store, settings), settings);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public Task Needs_initialization_true_for_empty_folder() => WithTempDirAsync((dir, _, _) =>
    {
        Assert.True(ProjectInitializer.NeedsInitialization(dir));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Needs_initialization_false_when_root_book_txt_exists() => WithTempDirAsync((dir, _, _) =>
    {
        File.WriteAllText(Path.Combine(dir, "Book.txt"), "a.md\n");
        Assert.False(ProjectInitializer.NeedsInitialization(dir));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Needs_initialization_false_when_manuscript_book_txt_exists() => WithTempDirAsync((dir, _, _) =>
    {
        Directory.CreateDirectory(Path.Combine(dir, "manuscript"));
        File.WriteAllText(Path.Combine(dir, "manuscript", "Book.txt"), "a.md\n");
        Assert.False(ProjectInitializer.NeedsInitialization(dir));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Initialize_lists_md_case_insensitively_sorted() => WithTempDirAsync(async (dir, init, _) =>
    {
        File.WriteAllText(Path.Combine(dir, "b story.md"), "x");
        File.WriteAllText(Path.Combine(dir, "A.MD"), "x");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllText(Path.Combine(dir, "x", "c.md"), "x");

        var result = await init.InitializeAsync(dir, ProjectKind.Novel);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "A.MD", "b story.md" }, File.ReadAllText(Path.Combine(dir, "Book.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.EndsWith("\n", File.ReadAllText(Path.Combine(dir, "Book.txt")));
    });

    [Theory]
    [InlineData(ProjectKind.Novel, "chapter-01.md")]
    [InlineData(ProjectKind.Story, "story.md")]
    [InlineData(ProjectKind.Anthology, "story-01.md")]
    public Task Initialize_empty_folder_creates_starter_per_kind(ProjectKind kind, string name) =>
        WithTempDirAsync(async (dir, init, _) =>
        {
            var result = await init.InitializeAsync(dir, kind);

            Assert.True(result.IsSuccess);
            Assert.True(File.Exists(Path.Combine(dir, name)));
            Assert.Equal("# Untitled\n", File.ReadAllText(Path.Combine(dir, name)));
            Assert.Equal(name + "\n", File.ReadAllText(Path.Combine(dir, "Book.txt")));
        });

    [Fact]
    public Task Initialize_writes_project_json() => WithTempDirAsync(async (dir, init, settings) =>
    {
        var result = await init.InitializeAsync(dir, ProjectKind.Anthology);

        Assert.True(result.IsSuccess);
        var loaded = await settings.LoadAsync(dir);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(ProjectKind.Anthology, loaded.Value.Kind);
        Assert.Equal(StorageMode.Sidecar, loaded.Value.Storage);
        Assert.True(Directory.Exists(Path.Combine(dir, ".hymnal-data")));
    });

    [Fact]
    public Task Initialize_fails_when_book_txt_exists() => WithTempDirAsync(async (dir, init, _) =>
    {
        File.WriteAllText(Path.Combine(dir, "Book.txt"), "a.md\n");

        var result = await init.InitializeAsync(dir, ProjectKind.Story);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(ProjectSettingsService.ProjectFilePath(dir)));
    });

    [Fact]
    public Task Initialize_fails_when_manuscript_book_txt_exists() => WithTempDirAsync(async (dir, init, _) =>
    {
        Directory.CreateDirectory(Path.Combine(dir, "manuscript"));
        File.WriteAllText(Path.Combine(dir, "manuscript", "Book.txt"), "a.md\n");
        File.WriteAllText(Path.Combine(dir, "x.md"), "x");

        var result = await init.InitializeAsync(dir, ProjectKind.Story);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(dir, "Book.txt")));
        Assert.False(File.Exists(ProjectSettingsService.ProjectFilePath(dir)));
    });
}
