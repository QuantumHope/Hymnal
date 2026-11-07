using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class StoryIdeaPromoterTests
{
    private static readonly ProjectProfile Anthology = ProjectProfile.For(new ProjectSettings(ProjectKind.Anthology));
    private static readonly ProjectProfile ShortStory = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));

    private sealed class FailingSaveIdeasService(IStoryIdeasService inner) : IStoryIdeasService
    {
        public Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot) => inner.LoadAsync(workspaceRoot);

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas) =>
            Task.FromResult(Result<Unit>.Fail("disk full"));
    }

    private sealed class Env
    {
        public required string Root { get; init; }
        public required string BookTxtPath { get; init; }
        public required string ManuscriptDir { get; init; }
        public MetadataStore Store { get; } = new();
        public StoryIdeasService Ideas => new(Store);
        public ChapterRegistryService Registry => new(Store);
        public PhaseDataService Phases => new(Store);
        public TargetsService Targets => new(Store);
        public NotesService Notes => new(Store);

        public StoryIdeaPromoter Promoter(IStoryIdeasService? ideas = null) =>
            new(new BookTxtStructureService(Store, new ExclusionManifestService(Store)),
                Registry, Phases, Targets, Notes, ideas ?? Ideas);
    }

    private static async Task WithEnvAsync(Func<Env, Task> body, bool subfolder = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "hymnal-promote-" + Guid.NewGuid().ToString("N"));
        var dir = subfolder ? Path.Combine(root, "manuscript") : root;
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Book.txt"), "one.md\n");
            File.WriteAllText(Path.Combine(dir, "one.md"), "# One\n");
            await body(new Env { Root = root, BookTxtPath = Path.Combine(dir, "Book.txt"), ManuscriptDir = dir });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static StoryIdea Idea(string title = "The Lighthouse", string premise = "A keeper.", string notes = "Some notes.",
        string? preset = "flash", IdeaState state = IdeaState.New, string id = "i1") =>
        new(id, title, premise, notes, [], preset, state, new DateOnly(2026, 1, 1), null);

    private static async Task<IReadOnlyList<StoryIdea>> SeedAsync(Env env, params StoryIdea[] ideas)
    {
        var save = await env.Ideas.SaveAsync(env.Root, ideas);
        Assert.True(save.IsSuccess, save.Error);
        return ideas;
    }

    [Theory]
    [InlineData("The Lighthouse", "the-lighthouse.md")]
    [InlineData("  Salt -- Debt! ", "salt-debt.md")]
    [InlineData("???", "untitled-story.md")]
    [InlineData("Café Noir", "caf-noir.md")]
    public void SuggestFileName_slugs_title(string title, string expected) =>
        Assert.Equal(expected, StoryIdeaPromoter.SuggestFileName(title));

    [Fact]
    public Task Promote_creates_file_and_appends_to_book_txt() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("the-lighthouse.md", result.Value);
        Assert.Equal("# The Lighthouse\n\n", File.ReadAllText(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        var entries = await new BookTxtStructureService(env.Store, new ExclusionManifestService(env.Store))
            .ReadNormalizedEntriesAsync(env.BookTxtPath);
        Assert.Equal(["one.md", "the-lighthouse.md"], entries.Value!);
    });

    [Fact]
    public Task Promote_sets_status_target_and_notes() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);
        Assert.True(result.IsSuccess, result.Error);

        var registry = await env.Registry.LoadAsync(env.Root);
        var entry = Assert.Single(registry.Values, e => e.CurrentPath == "the-lighthouse.md");
        var phases = await env.Phases.LoadAsync(env.Root);
        Assert.Equal(ChapterStatus.Planned, phases[entry.Uuid].Status);
        var targets = await env.Targets.LoadAsync(env.Root);
        Assert.Equal(0, targets[entry.Uuid].MinWords);
        Assert.Equal(1000, targets[entry.Uuid].MaxWords);
        Assert.Equal("flash", targets[entry.Uuid].Preset);
        var notes = await env.Notes.LoadAsync(INotesService.DeriveNotesPath(env.Root, "the-lighthouse.md"));
        Assert.Equal("A keeper.\n\nSome notes.", notes);
    });

    [Fact]
    public Task Promote_without_preset_writes_no_target() => WithEnvAsync(async env =>
    {
        var idea = Idea(preset: null);
        await SeedAsync(env, idea);
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, idea, "the-lighthouse.md", Anthology);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(await env.Targets.LoadAsync(env.Root));
    });

    [Fact]
    public Task Promote_with_empty_premise_writes_only_notes() => WithEnvAsync(async env =>
    {
        var idea = Idea(premise: "");
        await SeedAsync(env, idea);
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, idea, "the-lighthouse.md", Anthology);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Some notes.",
            await env.Notes.LoadAsync(INotesService.DeriveNotesPath(env.Root, "the-lighthouse.md")));
    });

    [Fact]
    public Task Promote_marks_idea_used_with_story_id() => WithEnvAsync(async env =>
    {
        var idea = Idea();
        var other = Idea(title: "Other", id: "i2");
        await SeedAsync(env, idea, other);
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, idea, "the-lighthouse.md", Anthology);
        Assert.True(result.IsSuccess, result.Error);

        var uuid = (await env.Registry.LoadAsync(env.Root)).Values.Single(e => e.CurrentPath == "the-lighthouse.md").Uuid;
        var loaded = (await env.Ideas.LoadAsync(env.Root)).Value!;
        var promoted = loaded.Single(i => i.Id == "i1");
        Assert.Equal(IdeaState.Used, promoted.State);
        Assert.Equal(uuid, promoted.StoryId);
        Assert.Equal(IdeaState.New, loaded.Single(i => i.Id == "i2").State);
    });

    [Fact]
    public Task Promote_in_manuscript_subfolder_creates_file_beside_book_txt() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("the-lighthouse.md", result.Value);
        Assert.True(File.Exists(Path.Combine(env.Root, "manuscript", "the-lighthouse.md")));
        Assert.False(File.Exists(Path.Combine(env.Root, "the-lighthouse.md")));
        Assert.Contains(INotesService.DeriveNotesPath(env.Root, "the-lighthouse.md"),
            Directory.GetFiles(Path.Combine(env.Root, ".hymnal-data", "notes")));
    }, subfolder: true);

    [Fact]
    public Task Promote_existing_file_fails_and_leaves_idea_unchanged() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        File.WriteAllText(Path.Combine(env.ManuscriptDir, "the-lighthouse.md"), "existing");
        var bookBefore = File.ReadAllText(env.BookTxtPath);

        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.Equal(bookBefore, File.ReadAllText(env.BookTxtPath));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        Assert.Equal(IdeaState.New, (await env.Ideas.LoadAsync(env.Root)).Value!.Single().State);
        Assert.DoesNotContain(await env.Registry.LoadAsync(env.Root), e => e.Value.CurrentPath == "the-lighthouse.md");
    });

    [Theory]
    [InlineData("")]
    [InlineData("a/b.md")]
    [InlineData("..\\x.md")]
    [InlineData("story.txt")]
    public Task Promote_rejects_bad_file_names(string fileName) => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var bookBefore = File.ReadAllText(env.BookTxtPath);

        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), fileName, Anthology);

        Assert.False(result.IsSuccess);
        Assert.Equal(bookBefore, File.ReadAllText(env.BookTxtPath));
        Assert.Equal(["Book.txt", "one.md"], Directory.GetFiles(env.ManuscriptDir).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Empty(await env.Registry.LoadAsync(env.Root));
        Assert.Equal(IdeaState.New, (await env.Ideas.LoadAsync(env.Root)).Value!.Single().State);
    });

    [Fact]
    public Task Promote_fails_for_short_story_profile() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", ShortStory);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
    });

    [Fact]
    public Task Promote_fails_for_used_idea() => WithEnvAsync(async env =>
    {
        var used = Idea(state: IdeaState.Used);
        await SeedAsync(env, used);
        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, used, "the-lighthouse.md", Anthology);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
    });

    [Fact]
    public Task Promote_failure_after_file_creation_leaves_idea_unchanged() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var promoter = env.Promoter(new FailingSaveIdeasService(env.Ideas));

        var result = await promoter.PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.Contains("the-lighthouse.md", result.Error);
        Assert.Contains("disk full", result.Error);
        Assert.Equal(IdeaState.New, (await env.Ideas.LoadAsync(env.Root)).Value!.Single().State);
    });
}
