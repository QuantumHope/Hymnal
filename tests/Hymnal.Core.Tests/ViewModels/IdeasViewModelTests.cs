using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class IdeasViewModelTests : IDisposable
{
    /// <summary>Contexts created by the running test; their file watchers are released in <see cref="Dispose"/>.</summary>
    private static readonly List<TestContext> LiveContexts = new();

    public void Dispose()
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
    }

    static IdeasViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    private static readonly DateOnly Day1 = new(2026, 1, 1);
    private static readonly DateOnly Day2 = new(2026, 2, 1);
    private static readonly DateOnly Day3 = new(2026, 3, 1);

    private static StoryIdea Idea(string id, string title, DateOnly created, IdeaState state = IdeaState.New,
        params string[] tags) =>
        new(id, title, "", "", tags, null, state, created, null);

    [Fact]
    public async Task Load_hides_used_and_shelved_by_default()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Alpha", Day1),
                Idea("b", "Bravo", Day1, IdeaState.Developing),
                Idea("c", "Charlie", Day1, IdeaState.Used),
                Idea("d", "Delta", Day1, IdeaState.Shelved));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                Assert.False(vm.ShowUsedAndShelved);
                Assert.Equal(["Alpha", "Bravo"], vm.Cards.Select(c => c.Title));

                vm.ShowUsedAndShelved = true;
                Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], vm.Cards.Select(c => c.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Cards_sorted_newest_first()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Old", Day1),
                Idea("b", "Zulu", Day3),
                Idea("c", "Middle", Day2),
                Idea("d", "Alpha", Day3));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                Assert.Equal(["Alpha", "Zulu", "Middle", "Old"], vm.Cards.Select(c => c.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Tag_filter_is_case_insensitive()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Ghost ship", Day1, IdeaState.New, "Horror", "sea"),
                Idea("b", "Love letter", Day2, IdeaState.New, "romance"));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                Assert.Equal(["Horror", "romance", "sea"], vm.AllTags);

                vm.TagFilter = "horror";
                Assert.Equal(["Ghost ship"], vm.Cards.Select(c => c.Title));

                vm.TagFilter = null;
                Assert.Equal(2, vm.Cards.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task New_idea_saves_and_selects()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                await vm.NewIdeaCommand.Execute(("The Lighthouse", "A keeper sees a ship that sank."));

                Assert.Equal("The Lighthouse", vm.Selected?.Title);
                Assert.Contains(vm.Selected, vm.Cards);
            });

            var saved = Assert.Single(await LoadFromDiskAsync(root));
            Assert.Equal("The Lighthouse", saved.Title);
            Assert.Equal("A keeper sees a ship that sank.", saved.Premise);
            Assert.Equal(IdeaState.New, saved.State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_debounce_into_one_save()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Draft", Day1));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();
                context.Ideas.SaveCount = 0;

                vm.Selected.Title = "First";
                vm.Selected.Title = "Second";
                vm.Selected.Title = "Third";
                context.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200));
                Assert.Equal(0, context.Ideas.SaveCount);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                await vm.FlushAsync();
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Third", Assert.Single(await LoadFromDiskAsync(root)).Title);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Changing_selection_flushes_pending_save()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "One", Day1), Idea("b", "Two", Day2));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                var one = vm.Cards.Single(c => c.Id == "a");
                vm.Selected = one;
                context.Ideas.SaveCount = 0;

                one.Notes = "Pending notes";
                vm.Selected = vm.Cards.Single(c => c.Id == "b");
                await vm.FlushAsync();
                Assert.Equal(1, context.Ideas.SaveCount);

                // The debounce window passing afterwards does not write again.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                await vm.FlushAsync();
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Pending notes", (await LoadFromDiskAsync(root)).Single(i => i.Id == "a").Notes);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Keep or toss", Day1));
            var context = await OpenAsync(root);
            var prompts = new List<string>();

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();

                vm.ConfirmAsync = message => { prompts.Add(message); return Task.FromResult(false); };
                await vm.DeleteSelectedCommand.Execute();
                Assert.Single(vm.Cards);
            });
            Assert.Single(prompts);
            Assert.Single(await LoadFromDiskAsync(root));

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();

                vm.ConfirmAsync = _ => Task.FromResult(true);
                await vm.DeleteSelectedCommand.Execute();
                Assert.Empty(vm.Cards);
                Assert.Null(vm.Selected);
            });
            Assert.Empty(await LoadFromDiskAsync(root));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Malformed_file_sets_read_only_and_never_writes()
    {
        var root = CreateWorkspace(AnthologyJson);
        var ideasPath = StoryIdeasService.IdeasFilePath(root);
        File.WriteAllText(ideasPath, "{ this is not json");
        var before = await File.ReadAllBytesAsync(ideasPath);
        try
        {
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();

                Assert.True(vm.IsReadOnly);
                Assert.Contains(IdeasViewModel.ReadOnlyNotice, context.Notifications.Infos);
                Assert.Empty(vm.Cards);
                Assert.False(await vm.NewIdeaCommand.CanExecute.FirstAsync());
                Assert.False(await vm.DeleteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(await vm.PromoteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(vm.CanPromote);

                await vm.NewIdeaAsync("Sneaky", "");
                await vm.FlushAsync();
                Assert.Equal(0, context.Ideas.SaveCount);
            });

            Assert.Equal(before, await File.ReadAllBytesAsync(ideasPath));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task CanPromote_false_for_short_story_profile()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();

                Assert.False(vm.CanPromote);
                Assert.False(await vm.PromoteSelectedCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_success_reloads_workspace_and_selects_story()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));
            var context = await OpenAsync(root);
            string? suggested = null;

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();
                Assert.True(vm.CanPromote);
                vm.AskFileNameAsync = suggestion => { suggested = suggestion; return Task.FromResult<string?>(suggestion); };

                await vm.PromoteSelectedCommand.Execute();

                Assert.Empty(context.Notifications.Errors);
                Assert.Equal("the-lighthouse.md", context.Workspace.SelectedNode?.Node.RelativePath);
                Assert.Empty(vm.Cards);

                vm.ShowUsedAndShelved = true;
                Assert.Equal(IdeaState.Used, vm.Cards.Single().State);
            });

            Assert.Equal("the-lighthouse.md", suggested);
            Assert.Equal("# The Lighthouse\n\n", File.ReadAllText(Path.Combine(root, "the-lighthouse.md")));
            Assert.Equal(IdeaState.Used, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_failure_shows_error()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                vm.Selected = vm.Cards.Single();
                // A name already on disk collides, so nothing is created.
                vm.AskFileNameAsync = _ => Task.FromResult<string?>("story-one.md");

                await vm.PromoteSelectedCommand.Execute();

                Assert.Single(context.Notifications.Errors);
                Assert.Equal(IdeaState.New, vm.Cards.Single().State);
            });

            Assert.Equal(IdeaState.New, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Profile_switch_to_novel_clears_cards()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Alpha", Day1));
            var context = await OpenAsync(root);

            await Ui(async () =>
            {
                var vm = await context.CreateIdeasAsync();
                Assert.Single(vm.Cards);

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Novel));
                Assert.True(save.IsSuccess, save.Error);
                var reload = await context.Workspace.ReloadProjectAsync();
                Assert.True(reload.IsSuccess, reload.Error);
                await vm.FlushAsync();

                Assert.Equal(ProjectKind.Novel, context.Workspace.Profile.Kind);
                Assert.Empty(vm.Cards);
                Assert.Null(vm.Selected);
                Assert.False(await vm.NewIdeaCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateWorkspace(string projectJson)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-ideas-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), "story-one.md\n");
        File.WriteAllText(Path.Combine(root, "story-one.md"), "# Story One\n");
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), projectJson);
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task SeedAsync(string root, params StoryIdea[] ideas)
    {
        var result = await new StoryIdeasService(new MetadataStore()).SaveAsync(root, ideas);
        Assert.True(result.IsSuccess, result.Error);
    }

    private static async Task<IReadOnlyList<StoryIdea>> LoadFromDiskAsync(string root)
    {
        var result = await new StoryIdeasService(new MetadataStore()).LoadAsync(root);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private static async Task<TestContext> OpenAsync(string root)
    {
        var context = new TestContext(root);
        await context.OpenAsync();
        await WaitUntilAsync(() => context.Workspace.Nodes.Count == 1,
            () => $"Workspace did not load. Errors: {string.Join(" | ", context.Notifications.Errors)}");
        return context;
    }

    private static Task Ui(Func<Task> action) =>
        RunOnUiThreadWithPumpingAsync(async () => { await action(); return 0; });

    private static Task<T> RunOnUiThreadWithPumpingAsync<T>(Func<Task<T>> action) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(action, TimeSpan.FromSeconds(40));

    private static async Task WaitUntilAsync(Func<bool> predicate, Func<string> failureMessage)
    {
        var satisfied = await RunOnUiThreadWithPumpingAsync(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && !predicate())
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            return predicate();
        });

        if (!satisfied)
            Assert.Fail(failureMessage());
    }

    private static Task? HydrationTask(WorkspaceViewModel workspace) =>
        typeof(WorkspaceViewModel)
            .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace) as Task;

    private sealed class TestContext : IDisposable
    {
        private readonly ManuscriptService _manuscript;
        private readonly StoryIdeaPromoter _promoter;
        private readonly List<IdeasViewModel> _ideasViewModels = new();

        public RecordingNotificationService Notifications { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public CountingIdeasService Ideas { get; }
        public HistoricalScheduler Scheduler { get; } = new();
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var store = new MetadataStore();
            var exclusions = new ExclusionManifestService(store);
            var registry = new ChapterRegistryService(store);
            var phases = new PhaseDataService(store);
            var targets = new TargetsService(store);
            var wordCount = new WordCountService();
            var structure = new BookTxtStructureService(store, exclusions, registry);
            ProjectSettings = new ProjectSettingsService(store);
            Ideas = new CountingIdeasService(new StoryIdeasService(store));
            _promoter = new StoryIdeaPromoter(structure, registry, phases, targets, new NotesService(store), Ideas);
            var editor = new EditorViewModel(store, Notifications, wordCount);
            _manuscript = new ManuscriptService(Notifications, exclusions);
            LiveContexts.Add(this);

            Workspace = new WorkspaceViewModel(
                _manuscript,
                structure,
                new StubFilePickerService(),
                new InMemoryAppSettingsStore(),
                new FixedFolderPickerService(folder),
                Notifications,
                editor,
                registry,
                phases,
                targets,
                wordCount,
                new WordCountHistoryService(store),
                exclusions,
                new OrphanFileDiscoveryService(),
                ProjectSettings,
                new ProjectInitializer(store, ProjectSettings),
                new NeverInitPrompt());
        }

        public void Dispose()
        {
            foreach (var vm in _ideasViewModels) vm.Dispose();
            _manuscript.Dispose();
        }

        public Task OpenAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
            return RunOnUiThreadWithPumpingAsync(async () =>
            {
                await (Task)method.Invoke(Workspace, Array.Empty<object>())!;
                // Wait for hydration inside this Dispatch: each Dispatch gets a fresh dispatcher, so
                // the node rebuild it posts would be lost once this job returns.
                if (HydrationTask(Workspace) is { } hydration)
                    await hydration;
                return 0;
            });
        }

        /// <summary>Creates and loads an ideas view model; call on the UI thread.</summary>
        public async Task<IdeasViewModel> CreateIdeasAsync()
        {
            var vm = new IdeasViewModel(Workspace, Ideas, _promoter, Notifications, Scheduler);
            _ideasViewModels.Add(vm);
            await vm.LoadAsync();
            return vm;
        }
    }

    private sealed class CountingIdeasService(IStoryIdeasService inner) : IStoryIdeasService
    {
        public int SaveCount { get; set; }

        public Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot) => inner.LoadAsync(workspaceRoot);

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas)
        {
            SaveCount++;
            return inner.SaveAsync(workspaceRoot, ideas);
        }
    }

    private sealed class NeverInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) { }
    }

    private sealed class InMemoryAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedFolderPickerService(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}
