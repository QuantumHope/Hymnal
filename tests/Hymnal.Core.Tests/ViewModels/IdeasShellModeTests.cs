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
using Avalonia.Controls;
using Hymnal.Infrastructure;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class IdeasShellModeTests : IDisposable
{
    private static readonly List<TestContext> LiveContexts = new();

    static IdeasShellModeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public void Dispose()
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
    }

    private const string NovelJson = "{ \"schemaVersion\": 1, \"kind\": \"novel\", \"storage\": \"sidecar\" }";
    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    [Fact]
    public async Task Ideas_mode_unavailable_for_novel()
    {
        var root = CreateWorkspace(NovelJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();

                Assert.False(window.IsIdeasModeAvailable);
                Assert.False(await window.SelectIdeasCommand.CanExecute.FirstAsync());
                Assert.False(await window.QuickCaptureIdeaCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Theory]
    [InlineData(StoryJson)]
    [InlineData(AnthologyJson)]
    public async Task Ideas_mode_available_for_story_and_anthology(string projectJson)
    {
        var root = CreateWorkspace(projectJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();

                Assert.True(window.IsIdeasModeAvailable);
                Assert.True(await window.SelectIdeasCommand.CanExecute.FirstAsync());
                Assert.True(await window.QuickCaptureIdeaCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Ideas_mode_unavailable_without_ideas_view_model()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow(withIdeas: false);
                await context.SettleAsync();

                Assert.Null(window.IdeasViewModel);
                Assert.False(window.IsIdeasModeAvailable);
                Assert.False(await window.SelectIdeasCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Selecting_ideas_sets_secondary_content_to_ideas_vm()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();

                await Within(window.SelectIdeasCommand.Execute().ToTask());

                Assert.Equal(ShellMode.Ideas, window.ActiveMode);
                Assert.True(window.IsIdeasVisible);
                Assert.False(window.IsEditorVisible);
                Assert.Same(window.IdeasViewModel, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_profile_to_novel_while_in_ideas_returns_to_write()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();
                await Within(window.SelectIdeasCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Ideas, window.ActiveMode);

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Novel));
                Assert.True(save.IsSuccess, save.Error);
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => window.ActiveMode == ShellMode.Write);

                Assert.False(window.IsIdeasModeAvailable);
                Assert.False(window.IsIdeasVisible);
                Assert.True(window.IsEditorVisible);
                Assert.Null(window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Quick_capture_creates_new_idea()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();
                window.ShowQuickIdeaDialogAsync = () =>
                    Task.FromResult<(string Title, string Premise)?>(("The Lighthouse", "A keeper sees a ship that sank."));

                await Within(window.QuickCaptureIdeaCommand.Execute().ToTask());
                await Within(window.FlushPendingEditsAsync());

                // Capturing doesn't leave the current view.
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.Equal("The Lighthouse", window.IdeasViewModel!.Cards.Single().Title);
            });

            var saved = Assert.Single(await LoadFromDiskAsync(root));
            Assert.Equal("The Lighthouse", saved.Title);
            Assert.Equal("A keeper sees a ship that sank.", saved.Premise);
            Assert.Equal(IdeaState.New, saved.State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Quick_capture_cancel_creates_nothing()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();
                var asked = false;
                window.ShowQuickIdeaDialogAsync = () =>
                {
                    asked = true;
                    return Task.FromResult<(string Title, string Premise)?>(null);
                };

                await Within(window.QuickCaptureIdeaCommand.Execute().ToTask());
                await Within(window.FlushPendingEditsAsync());

                Assert.True(asked);
                Assert.Empty(window.IdeasViewModel!.Cards);
            });

            Assert.False(File.Exists(Path.Combine(root, ".hymnal-data", "ideas.json")));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Flushing_pending_edits_on_close_writes_unsaved_idea_edits()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            var seeded = await new StoryIdeasService(new MetadataStore()).SaveAsync(root,
                [new StoryIdea("a", "Draft", "", "", [], null, IdeaState.New, new DateOnly(2026, 1, 1), null)]);
            Assert.True(seeded.IsSuccess, seeded.Error);

            await Ui(root, async context =>
            {
                var window = context.CreateMainWindow();
                await context.SettleAsync();
                var ideas = window.IdeasViewModel!;
                ideas.Selected = ideas.Cards.Single();

                // The debounce never fires: the test scheduler is not advanced.
                ideas.Selected.Notes = "Typed just before closing";
                await Within(window.FlushPendingEditsAsync());
            });

            Assert.Equal("Typed just before closing", Assert.Single(await LoadFromDiskAsync(root)).Notes);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Ideas_view_state_picker_shows_used_and_keeps_selection_when_options_change()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            var day = new DateOnly(2026, 1, 1);
            var seeded = await new StoryIdeasService(new MetadataStore()).SaveAsync(root,
            [
                new StoryIdea("u1", "Used one", "", "", [], null, IdeaState.Used, day, "story-uuid"),
                new StoryIdea("n", "Fresh", "", "", [], null, IdeaState.New, day.AddDays(1), null),
                new StoryIdea("u2", "Used two", "", "", [], null, IdeaState.Used, day.AddDays(2), "other-uuid"),
            ]);
            Assert.True(seeded.IsSuccess, seeded.Error);

            await Ui(root, async context =>
            {
                var mainWindow = context.CreateMainWindow();
                await context.SettleAsync();
                var ideas = mainWindow.IdeasViewModel!;
                ideas.ShowUsedAndShelved = true;

                var view = new Hymnal.Views.IdeasView { DataContext = ideas };
                var host = new Avalonia.Controls.Window { Content = view, Width = 1280, Height = 800 };
                host.Show();
                try
                {
                    var statePicker = view.FindControl<Avalonia.Controls.ComboBox>("IdeaStatePicker")!;
                    var targetPicker = view.FindControl<Avalonia.Controls.ComboBox>("IdeaTargetPicker")!;
                    var used1 = ideas.Cards.Single(c => c.Id == "u1");
                    var fresh = ideas.Cards.Single(c => c.Id == "n");
                    var used2 = ideas.Cards.Single(c => c.Id == "u2");

                    ideas.Selected = used1;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(IdeaState.Used, statePicker.SelectedItem);
                    Assert.Same(IdeasViewModel.NoTarget, targetPicker.SelectedItem);

                    // Used → Developing swaps the picker's options; the new state stays selected.
                    statePicker.SelectedItem = IdeaState.Developing;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(IdeaState.Developing, used1.State);
                    Assert.Same(IdeaCardViewModel.SelectableStates, statePicker.ItemsSource);
                    Assert.Equal(IdeaState.Developing, statePicker.SelectedItem);

                    // Moving between cards with different options shows each card's own state
                    // and changes neither.
                    ideas.Selected = fresh;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(IdeaState.New, statePicker.SelectedItem);

                    ideas.Selected = used2;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(IdeaState.Used, statePicker.SelectedItem);

                    ideas.Selected = fresh;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(IdeaState.New, statePicker.SelectedItem);

                    Assert.Equal(IdeaState.New, fresh.State);
                    Assert.Equal(IdeaState.Used, used2.State);
                    Assert.Equal(IdeaState.Developing, used1.State);
                    Assert.Null(fresh.TargetPreset);

                    // The target picker sets the selected card's preset, and only that card's.
                    var preset = ideas.TargetOptions[1];
                    targetPicker.SelectedItem = preset;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Equal(preset.Key, fresh.TargetPreset);

                    ideas.Selected = used2;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Same(IdeasViewModel.NoTarget, targetPicker.SelectedItem);
                    ideas.Selected = fresh;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Assert.Same(preset, targetPicker.SelectedItem);
                    Assert.Null(used2.TargetPreset);
                    Assert.Equal(preset.Key, fresh.TargetPreset);
                }
                finally
                {
                    host.Close();
                }

                await Within(mainWindow.FlushPendingEditsAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

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

    private static string CreateWorkspace(string projectJson)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-ideas-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), "story-one.md\n");
        File.WriteAllText(Path.Combine(root, "story-one.md"), "# Story One\n");
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), projectJson);
        return root;
    }

    private static void DeleteFolder(string root)
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task<IReadOnlyList<StoryIdea>> LoadFromDiskAsync(string root)
    {
        var result = await new StoryIdeasService(new MetadataStore()).LoadAsync(root);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    /// <summary>Opens <paramref name="root"/> and runs <paramref name="body"/> in one UI Dispatch.</summary>
    private static Task Ui(string root, Func<TestContext, Task> body) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var context = new TestContext(root);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == 1);
            await body(context);
            return 0;
        }, TimeSpan.FromSeconds(40));

    private sealed class TestContext : IDisposable
    {
        private readonly ManuscriptService _manuscript;
        private readonly MetadataStore _store = new();
        private readonly StoryIdeaPromoter _promoter;
        private readonly StoryIdeasService _ideas;
        private IdeasViewModel? _ideasVm;
        private readonly List<IDisposable> _windowParts = new();

        public NotificationService Notifications { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public HistoricalScheduler Scheduler { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }
        private readonly InMemoryAppSettingsStore _settings = new();
        private readonly PhaseDataService _phases;
        private readonly TargetsService _targets;
        private readonly BookTxtStructureService _structure;

        public TestContext(string folder)
        {
            var exclusions = new ExclusionManifestService(_store);
            var registry = new ChapterRegistryService(_store);
            _phases = new PhaseDataService(_store);
            _targets = new TargetsService(_store);
            var wordCount = new WordCountService();
            _structure = new BookTxtStructureService(_store, exclusions, registry);
            ProjectSettings = new ProjectSettingsService(_store);
            _ideas = new StoryIdeasService(_store);
            _promoter = new StoryIdeaPromoter(_structure, registry, _phases, _targets, new NotesService(_store), _ideas);
            Editor = new EditorViewModel(_store, Notifications, wordCount);
            _manuscript = new ManuscriptService(Notifications, exclusions);
            LiveContexts.Add(this);

            Workspace = new WorkspaceViewModel(
                _manuscript,
                _structure,
                new StubFilePickerService(),
                _settings,
                new FixedFolderPickerService(folder),
                Notifications,
                Editor,
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
        }

        /// <summary>Releases view models and their file watchers (inotify instances are a scarce per-user limit).</summary>
        public void Dispose()
        {
            _ideasVm?.Dispose();
            foreach (var part in _windowParts) part.Dispose();
            _windowParts.Clear();
            Editor.Dispose();
            _manuscript.Dispose();
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

        /// <summary>Runs posted change handlers and waits for the ideas load they start.</summary>
        public async Task SettleAsync()
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (_ideasVm != null)
            {
                await Within(_ideasVm.LoadAsync());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Within((Task)typeof(IdeasViewModel)
                    .GetField("_currentLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(_ideasVm)!);
            }
        }

        private T Track<T>(T part) where T : IDisposable
        {
            _windowParts.Add(part);
            return part;
        }

        public MainWindowViewModel CreateMainWindow(bool withIdeas = true)
        {
            if (withIdeas)
                _ideasVm = new IdeasViewModel(Workspace, _ideas, _promoter, Notifications, Scheduler);

            var docs = new SupplementalDocsViewModel(
                Workspace, new SupplementalDocsService(_store), Editor, Notifications, _settings, new StubFilePickerService());
            var history = new WordCountHistoryService(_store);

            return new MainWindowViewModel(
                Workspace,
                Editor,
                Track(new NotesViewModel(Editor, Workspace, new NotesService(_store), Notifications, _settings)),
                Track(new ChapterInfoViewModel(Editor, Workspace, _phases, _targets, _settings, Notifications, history)),
                new BookStatsViewModel(Workspace, history, _targets),
                new GanttViewModel(Workspace, _phases, Notifications),
                Track(new CorkboardViewModel(Workspace, _structure, new OrphanFileDiscoveryService(), _settings, Notifications, _manuscript)),
                new ResearchViewModel(Workspace, docs, Editor, AiTestDoubles.CreateStubAiChatViewModel(Editor, Workspace, Notifications)),
                docs,
                Track(new GitPanelViewModel(Workspace, Editor, new NoOpGitService(), Notifications)),
                Notifications,
                _settings,
                AiTestDoubles.CreateStubAiChatViewModel(Editor, Workspace, Notifications),
                AiTestDoubles.CreateStubSettingsFactory(Notifications),
                _ideasVm);
        }
    }

    private sealed class NeverInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    /// <summary>
    /// Never reports a last workspace, so the main window's startup restore doesn't reopen the
    /// workspace the test already opened.
    /// </summary>
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
