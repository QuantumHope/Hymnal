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

    /// <summary>
    /// After the first (pre-flight) load, another writer adds an idea; later loads can be made to fail.
    /// </summary>
    private sealed class ConcurrentWriterIdeasService(IStoryIdeasService inner, StoryIdea added) : IStoryIdeasService
    {
        public int Loads { get; private set; }
        public bool FailLaterLoads { get; init; }

        public async Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot)
        {
            Loads++;
            if (Loads > 1 && FailLaterLoads)
                return Result<IReadOnlyList<StoryIdea>>.Fail("ideas.json is locked");
            var result = await inner.LoadAsync(workspaceRoot);
            if (Loads == 1)
            {
                var save = await inner.SaveAsync(workspaceRoot, [.. result.Value!, added]);
                Assert.True(save.IsSuccess, save.Error);
            }
            return result;
        }

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas) =>
            inner.SaveAsync(workspaceRoot, ideas);
    }

    private sealed class ThrowingNotesService : INotesService
    {
        public Task<string> LoadAsync(string absoluteNotesPath) => Task.FromResult("");
        public Task SaveAsync(string absoluteNotesPath, string content) => throw new IOException("notes boom");
        public Task DeleteAsync(string absoluteNotesPath) => throw new IOException("notes boom");
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

        public BoundPromoter Promoter(IStoryIdeasService? ideas = null, INotesService? notes = null) =>
            new(new StoryIdeaPromoter(new BookTxtStructureService(Store, new ExclusionManifestService(Store)),
                    Registry, ideas ?? Ideas),
                new SidecarStoryMetadataStore(Phases, Targets, notes ?? Notes, new BeatBoardService(Store)));
    }

    /// <summary>A promoter with the workspace's (sidecar) metadata store it promotes into.</summary>
    private sealed class BoundPromoter(StoryIdeaPromoter promoter, IStoryMetadataStore metadata)
    {
        public Task<PromoteResult> PromoteAsync(
            string root, string bookTxtPath, StoryIdea idea, string fileName, ProjectProfile profile) =>
            promoter.PromoteAsync(root, bookTxtPath, idea, fileName, profile, metadata);
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
        Assert.True(result.FileCreated);
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
        Assert.False(result.FileCreated);
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
        Assert.True(result.FileCreated);
        Assert.Contains("the-lighthouse.md", result.Error);
        Assert.Contains("disk full", result.Error);
        Assert.Equal(IdeaState.New, (await env.Ideas.LoadAsync(env.Root)).Value!.Single().State);
        Assert.True(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        Assert.Contains(await env.Registry.LoadAsync(env.Root), e => e.Value.CurrentPath == "the-lighthouse.md");
    });

    [Fact]
    public Task Promote_throwing_step_after_creation_fails_naming_file() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var result = await env.Promoter(notes: new ThrowingNotesService())
            .PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.True(result.FileCreated);
        Assert.Contains("the-lighthouse.md", result.Error);
        Assert.Contains("notes boom", result.Error);
        Assert.Equal(IdeaState.New, (await env.Ideas.LoadAsync(env.Root)).Value!.Single().State);
    });

    [Fact]
    public Task Promote_malformed_ideas_file_fails_and_writes_nothing() => WithEnvAsync(async env =>
    {
        var path = StoryIdeasService.IdeasFilePath(env.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var bookBefore = File.ReadAllText(env.BookTxtPath);

        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.False(result.FileCreated);
        Assert.Equal(bookBefore, File.ReadAllText(env.BookTxtPath));
        Assert.False(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        Assert.Equal("{ not json", File.ReadAllText(path));
    });

    [Fact]
    public Task Promote_idea_missing_from_file_fails_and_writes_nothing() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea(id: "other"));
        var bookBefore = File.ReadAllText(env.BookTxtPath);

        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.Equal(bookBefore, File.ReadAllText(env.BookTxtPath));
        Assert.False(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        Assert.Empty(await env.Registry.LoadAsync(env.Root));
    });

    [Fact]
    public Task Promote_persisted_used_idea_fails_despite_stale_new_copy() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea(state: IdeaState.Used));
        var bookBefore = File.ReadAllText(env.BookTxtPath);

        var result = await env.Promoter().PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.Equal(bookBefore, File.ReadAllText(env.BookTxtPath));
        Assert.False(File.Exists(Path.Combine(env.ManuscriptDir, "the-lighthouse.md")));
        Assert.Empty(await env.Registry.LoadAsync(env.Root));
    });

    [Fact]
    public Task Promote_keeps_ideas_added_by_another_writer_after_pre_flight() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var ideas = new ConcurrentWriterIdeasService(env.Ideas, Idea(title: "Written meanwhile", id: "i2"));

        var result = await env.Promoter(ideas).PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, ideas.Loads);
        var loaded = (await env.Ideas.LoadAsync(env.Root)).Value!;
        Assert.Equal(IdeaState.Used, loaded.Single(i => i.Id == "i1").State);
        Assert.NotNull(loaded.Single(i => i.Id == "i1").StoryId);
        var meanwhile = loaded.Single(i => i.Id == "i2");
        Assert.Equal("Written meanwhile", meanwhile.Title);
        Assert.Equal(IdeaState.New, meanwhile.State);
    });

    [Fact]
    public Task Promote_fails_after_creation_when_ideas_cannot_be_re_read() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var ideas = new ConcurrentWriterIdeasService(env.Ideas, Idea(title: "Written meanwhile", id: "i2")) { FailLaterLoads = true };

        var result = await env.Promoter(ideas).PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.True(result.FileCreated);
        Assert.Contains("the-lighthouse.md", result.Error);
        Assert.Contains("ideas.json is locked", result.Error);
        // The pre-creation snapshot was never written back over the other writer's idea.
        var loaded = (await env.Ideas.LoadAsync(env.Root)).Value!;
        Assert.Equal(["i1", "i2"], loaded.Select(i => i.Id).Order().ToArray());
        Assert.All(loaded, i => Assert.Equal(IdeaState.New, i.State));
    });

    [Fact]
    public Task Promote_fails_after_creation_when_idea_was_deleted_meanwhile() => WithEnvAsync(async env =>
    {
        await SeedAsync(env, Idea());
        var promoter = env.Promoter(notes: new DeletingNotesService(env));

        var result = await promoter.PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

        Assert.False(result.IsSuccess);
        Assert.True(result.FileCreated);
        Assert.Contains("no longer", result.Error);
        Assert.Empty((await env.Ideas.LoadAsync(env.Root)).Value!);
    });

    [Theory]
    [InlineData(IdeaState.Used, "other-story")]
    [InlineData(IdeaState.Used, null)]
    [InlineData(IdeaState.Developing, "other-story")]
    public Task Promote_fails_after_creation_when_idea_was_used_meanwhile(IdeaState stateMeanwhile, string? storyIdMeanwhile) =>
        WithEnvAsync(async env =>
        {
            await SeedAsync(env, Idea());
            var promoter = env.Promoter(notes: new RewritingNotesService(env,
                i => i with { State = stateMeanwhile, StoryId = storyIdMeanwhile }));

            var result = await promoter.PromoteAsync(env.Root, env.BookTxtPath, Idea(), "the-lighthouse.md", Anthology);

            Assert.False(result.IsSuccess);
            Assert.True(result.FileCreated);
            Assert.Contains("the-lighthouse.md", result.Error);
            Assert.Contains("used by another story", result.Error);
            // The other writer's state and StoryId are left as they wrote them.
            var loaded = Assert.Single((await env.Ideas.LoadAsync(env.Root)).Value!);
            Assert.Equal(stateMeanwhile, loaded.State);
            Assert.Equal(storyIdMeanwhile, loaded.StoryId);
        });

    /// <summary>Saving notes also rewrites every idea, as if another writer changed it mid-promote.</summary>
    private sealed class RewritingNotesService(Env env, Func<StoryIdea, StoryIdea> rewrite) : INotesService
    {
        public Task<string> LoadAsync(string absoluteNotesPath) => Task.FromResult("");

        public Task DeleteAsync(string absoluteNotesPath) => env.Notes.DeleteAsync(absoluteNotesPath);

        public async Task SaveAsync(string absoluteNotesPath, string content)
        {
            await env.Notes.SaveAsync(absoluteNotesPath, content);
            var current = await env.Ideas.LoadAsync(env.Root);
            Assert.True(current.IsSuccess, current.Error);
            var save = await env.Ideas.SaveAsync(env.Root, current.Value!.Select(rewrite).ToList());
            Assert.True(save.IsSuccess, save.Error);
        }
    }

    /// <summary>Saving notes also deletes every idea, as if another writer removed the idea mid-promote.</summary>
    private sealed class DeletingNotesService(Env env) : INotesService
    {
        public Task<string> LoadAsync(string absoluteNotesPath) => Task.FromResult("");

        public Task DeleteAsync(string absoluteNotesPath) => env.Notes.DeleteAsync(absoluteNotesPath);

        public async Task SaveAsync(string absoluteNotesPath, string content)
        {
            await env.Notes.SaveAsync(absoluteNotesPath, content);
            var save = await env.Ideas.SaveAsync(env.Root, []);
            Assert.True(save.IsSuccess, save.Error);
        }
    }
}
