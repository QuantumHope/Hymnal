using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class PlanHostViewModelTests
{
    static PlanHostViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string NovelJson = "{ \"schemaVersion\": 1, \"kind\": \"novel\", \"storage\": \"sidecar\" }";
    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    // Anthology layout: one single-file story, then a long story told as a Part with two scenes.
    private const string Lighthouse = "lighthouse.md";
    private const string LongPart = "long-story/part.md";
    private const string LongSceneA = "long-story/scene-a.md";
    private const string LongSceneB = "long-story/scene-b.md";

    [Fact]
    public async Task Novel_plan_content_is_corkboard()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);
                Assert.Null(context.Board.BoardKey);

                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.Same(context.Corkboard, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_plan_opens_story_board()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Same(context.Board, host.Content);
                Assert.Equal("The Keeper", context.Board.BoardTitle);
                Assert.Equal([new SceneOption("the-keeper.md", "The Keeper")], context.Board.SceneOptions);
                Assert.False(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);

                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.Same(host, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_with_two_files_shows_files_strip()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Same(context.Board, host.Content);
                Assert.True(host.ShowFilesStrip);
                Assert.False(host.IsFilesStripExpanded);
                Assert.Same(context.Corkboard, host.Corkboard);
                Assert.Equal("Scene One", context.Board.BoardTitle);
                Assert.Equal(["scene-one.md", "scene-two.md"], context.Board.SceneOptions.Select(o => o.RelativePath));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_beats_request_drills_in_and_back_returns()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();
                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.Equal("Collection", host.BreadcrumbParent);
                Assert.False(await host.BackToCollectionCommand.CanExecute.FirstAsync());

                var story = context.Node(Lighthouse);
                var card = await context.CardAsync(Lighthouse);
                await Within(context.Corkboard.OpenCardCommand.Execute(card).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == story.Uuid);

                Assert.Same(context.Board, host.Content);
                Assert.True(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);
                Assert.Equal("Lighthouse", context.Board.BoardTitle);
                Assert.Equal([new SceneOption(Lighthouse, "Lighthouse")], context.Board.SceneOptions);

                // An edit still waiting on its debounce is written to this story's board on the way out.
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "Storm arrives";

                await Within(host.BackToCollectionCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                var saved = await new BeatBoardService(new MetadataStore()).LoadAsync(root, story.Uuid);
                Assert.True(saved.IsSuccess, saved.Error);
                Assert.Equal("Storm arrives", Assert.Single(saved.Value!).Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_part_story_scope_is_its_chapters()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();

                var part = context.Node(LongPart);
                var divider = await context.PartDividerAsync(LongPart);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(divider).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);

                Assert.Same(context.Board, host.Content);
                Assert.Equal("The Long Story", context.Board.BoardTitle);
                Assert.Equal([LongSceneA, LongSceneB], context.Board.SceneOptions.Select(o => o.RelativePath));

                // A scene's Beats action opens the board of the story it belongs to.
                await Within(host.BackToCollectionCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null);
                var scene = await context.CardAsync(LongSceneB);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(scene).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_double_click_requests_beats_not_editor()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var beats = new List<ChapterViewModel>();
                var opened = new List<ChapterViewModel>();
                using var beatsSub = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                using var openSub = context.Corkboard.OpenChapterRequested.Subscribe(opened.Add);
                await context.SettleAsync();
                Assert.True(context.Corkboard.IsAnthology);

                // Story card and Part divider: double-click drills into beats.
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.PartDividerAsync(LongPart)).ToTask());
                Assert.Equal([Lighthouse, LongPart], beats.Select(b => b.Node.RelativePath));
                Assert.Empty(opened);

                // A scene inside a long story opens in the editor; Open always does.
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.CardAsync(LongSceneA)).ToTask());
                await Within(context.Corkboard.OpenInEditorCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                Assert.Equal([LongSceneA, Lighthouse], opened.Select(o => o.Node.RelativePath));
                Assert.Equal(2, beats.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_double_click_still_opens_editor()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var beats = new List<ChapterViewModel>();
                var opened = new List<ChapterViewModel>();
                using var beatsSub = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                using var openSub = context.Corkboard.OpenChapterRequested.Subscribe(opened.Add);
                await context.SettleAsync();
                Assert.False(context.Corkboard.IsAnthology);

                var card = await context.CardAsync("chapter-one.md");
                await Within(context.Corkboard.OpenCardCommand.Execute(card).ToTask());
                await Within(context.Corkboard.ShowBeatsCommand.Execute(card).ToTask());

                Assert.Equal(["chapter-one.md"], opened.Select(o => o.Node.RelativePath));
                Assert.Empty(beats);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_to_novel_returns_plan_to_corkboard_and_closes_board()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.Same(host, window.SecondaryCentreContent);

                var story = context.Node(Lighthouse);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == story.Uuid);

                var changed = 0;
                window.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SecondaryCentreContent))
                        changed++;
                };

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Novel));
                Assert.True(save.IsSuccess, save.Error);
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.Same(context.Corkboard, window.SecondaryCentreContent);
                Assert.True(changed > 0);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_story_to_anthology_closes_story_board()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Anthology));
                Assert.True(save.IsSuccess, save.Error);
                await Within(context.Workspace.ReloadProjectAsync());
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_workspace_closes_board_and_shows_corkboard()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                await Within(context.Workspace.CloseWorkspaceCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null && ReferenceEquals(host.Content, context.Corkboard));

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowFilesStrip);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Close_flush_writes_pending_beat_edits()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                var window = context.CreateMainWindow(host);
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Note = "Typed just before closing";

                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(TimeSpan.FromSeconds(5))));
            });

            var saved = await new BeatBoardService(new MetadataStore()).LoadAsync(root, BeatBoardService.ShortStoryBoardKey);
            Assert.True(saved.IsSuccess, saved.Error);
            Assert.Equal("Typed just before closing", Assert.Single(saved.Value!).Note);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private static string CreateAnthology() =>
        CreateWorkspace(AnthologyJson,
            (Lighthouse, "# Lighthouse\n\nThe lamp turns.\n"),
            (LongPart, "{class: part}\n# The Long Story\n"),
            (LongSceneA, "# Scene A\n\nArrival.\n"),
            (LongSceneB, "# Scene B\n\nDeparture.\n"));

    private static string CreateWorkspace(string projectJson, params (string Path, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-plan-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), string.Join("\n", files.Select(f => f.Path)) + "\n");
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), projectJson);
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Pumps the current UI dispatcher until <paramref name="predicate"/> holds; call on the UI thread.</summary>
    private static async Task PumpUntilAsync(Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string? what = null)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static async Task Within(Task task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        if (await Task.WhenAny(task, Task.Delay(StepTimeout)) != task)
            Assert.Fail($"Timed out waiting for {what}.");
        await task;
    }

    private static async Task<T> Within<T>(Task<T> task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        await Within((Task)task, what);
        return await task;
    }

    /// <summary>
    /// Opens <paramref name="root"/> and runs <paramref name="body"/> in one UI Dispatch; watcher owners
    /// and view models are disposed before the Dispatch ends.
    /// </summary>
    private static Task Ui(string root, int nodeCount, Func<TestContext, Task> body) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var context = new TestContext(root);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == nodeCount);
            await body(context);
            return 0;
        }, TimeSpan.FromSeconds(40));

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        private readonly MetadataStore _store = new();
        private readonly InMemoryAppSettingsStore _settings = new();
        private readonly ManuscriptService _manuscript;
        private readonly EditorViewModel _editor;
        private readonly PhaseDataService _phases;
        private readonly TargetsService _targets;
        private readonly BookTxtStructureService _structure;

        public NotificationService Notifications { get; } = new();
        public HistoricalScheduler Scheduler { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public WorkspaceViewModel Workspace { get; }
        public CorkboardViewModel Corkboard { get; }
        public BeatBoardViewModel Board { get; }

        public TestContext(string folder)
        {
            var exclusions = new ExclusionManifestService(_store);
            var registry = new ChapterRegistryService(_store);
            var wordCount = new WordCountService();
            _phases = new PhaseDataService(_store);
            _targets = new TargetsService(_store);
            _structure = new BookTxtStructureService(_store, exclusions, registry);
            ProjectSettings = new ProjectSettingsService(_store);
            _editor = _owned.Track(new EditorViewModel(_store, Notifications, wordCount));
            _manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));

            Workspace = new WorkspaceViewModel(
                _manuscript,
                _structure,
                new StubFilePickerService(),
                _settings,
                new FixedFolderPickerService(folder),
                Notifications,
                _editor,
                registry,
                _phases,
                _targets,
                wordCount,
                new WordCountHistoryService(_store),
                exclusions,
                new OrphanFileDiscoveryService(),
                ProjectSettings,
                new ProjectInitializer(_store, ProjectSettings),
                new NeverInitPrompt());

            Corkboard = _owned.Track(new CorkboardViewModel(
                Workspace, _structure, new OrphanFileDiscoveryService(), _settings, Notifications, _manuscript));
            Board = _owned.Track(new BeatBoardViewModel(Workspace, new BeatBoardService(_store), Notifications, Scheduler));
        }

        public void Dispose() => _owned.Dispose();

        public PlanHostViewModel CreateHost() =>
            _owned.Track(new PlanHostViewModel(Workspace, Corkboard, Board, Notifications));

        public MainWindowViewModel CreateMainWindow(PlanHostViewModel host)
        {
            var docs = new SupplementalDocsViewModel(
                Workspace, new SupplementalDocsService(_store), _editor, Notifications, _settings, new StubFilePickerService());
            var history = new WordCountHistoryService(_store);

            return new MainWindowViewModel(
                Workspace,
                _editor,
                _owned.Track(new NotesViewModel(_editor, Workspace, new NotesService(_store), Notifications, _settings)),
                _owned.Track(new ChapterInfoViewModel(_editor, Workspace, _phases, _targets, _settings, Notifications, history)),
                new BookStatsViewModel(Workspace, history, _targets),
                new GanttViewModel(Workspace, _phases, Notifications),
                Corkboard,
                new ResearchViewModel(Workspace, docs, _editor, AiTestDoubles.CreateStubAiChatViewModel(_editor, Workspace, Notifications)),
                docs,
                _owned.Track(new GitPanelViewModel(Workspace, _editor, new NoOpGitService(), Notifications)),
                Notifications,
                _settings,
                AiTestDoubles.CreateStubAiChatViewModel(_editor, Workspace, Notifications),
                AiTestDoubles.CreateStubSettingsFactory(Notifications),
                ideasViewModel: null,
                planHostViewModel: host);
        }

        public ChapterViewModel Node(string relativePath) =>
            Workspace.Nodes.Single(n => n.Node.RelativePath == relativePath);

        /// <summary>Runs posted handlers so the host and board have caught up with the workspace.</summary>
        public async Task SettleAsync()
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public async Task<ChapterCardItemViewModel> CardAsync(string relativePath)
        {
            await PumpUntilAsync(() => Corkboard.Items.OfType<ChapterCardItemViewModel>().Any(i => i.RelativePath == relativePath));
            return Corkboard.Items.OfType<ChapterCardItemViewModel>().First(i => i.RelativePath == relativePath);
        }

        public async Task<PartDividerItemViewModel> PartDividerAsync(string relativePath)
        {
            await PumpUntilAsync(() => Corkboard.Items.OfType<PartDividerItemViewModel>().Any(i => i.RelativePath == relativePath));
            return Corkboard.Items.OfType<PartDividerItemViewModel>().First(i => i.RelativePath == relativePath);
        }

        public async Task OpenWorkspaceInCurrentDispatchAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
            await Within((Task)method.Invoke(Workspace, Array.Empty<object>())!);
            if (typeof(WorkspaceViewModel)
                    .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Workspace) is Task hydration)
                await Within(hydration);
        }
    }

    private sealed class NeverInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    /// <summary>Never reports a last workspace, so the main window's startup restore doesn't reopen one.</summary>
    private sealed class InMemoryAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(key != "lastWorkspacePath" && _values.TryGetValue(key, out var value) && value is T typed ? typed : default);

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
