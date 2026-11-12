using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Xunit;

namespace Hymnal.Core.Tests.Infrastructure;

/// <summary>
/// Shared scaffolding for Plan-mode tests (the plan host, beat board and their views): temp workspaces,
/// UI pumping helpers and a <see cref="PlanTestContext"/> wiring a real workspace, Corkboard and board.
/// </summary>
internal static class PlanTestHarness
{
    public const string NovelJson = "{ \"schemaVersion\": 1, \"kind\": \"novel\", \"storage\": \"sidecar\" }";
    public const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    public const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates a workspace whose Book.txt lists <paramref name="files"/> in order.</summary>
    public static string CreateWorkspace(string projectJson, params (string Path, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-plan-{Guid.NewGuid():N}");
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

    public static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Pumps the current UI dispatcher until <paramref name="predicate"/> holds; call on the UI thread.</summary>
    public static async Task PumpUntilAsync(Func<bool> predicate,
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

    public static async Task Within(Task task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        if (await Task.WhenAny(task, Task.Delay(StepTimeout)) != task)
            Assert.Fail($"Timed out waiting for {what}.");
        await task;
    }

    public static async Task<T> Within<T>(Task<T> task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        await Within((Task)task, what);
        return await task;
    }

    /// <summary>
    /// Opens <paramref name="root"/> and runs <paramref name="body"/> in one UI Dispatch (each Dispatch gets a
    /// fresh dispatcher, so opening, hydration and the test's UI work must share a job); watcher owners and
    /// view models are disposed before the Dispatch ends.
    /// </summary>
    public static Task Ui(string root, int nodeCount, Func<PlanTestContext, Task> body,
        Func<IMetadataStore, IBeatBoardService>? boards = null, IMetadataStore? store = null) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var context = new PlanTestContext(root, boards, store);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == nodeCount);
            await body(context);
            return 0;
        }, TimeSpan.FromSeconds(40));

    public static async Task<IReadOnlyList<Beat>> LoadFromDiskAsync(string root, string key)
    {
        var result = await new BeatBoardService(new MetadataStore()).LoadAsync(root, key);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    public static async Task SeedAsync(string root, string key, params Beat[] beats)
    {
        var result = await new BeatBoardService(new MetadataStore()).SaveAsync(root, key, beats);
        Assert.True(result.IsSuccess, result.Error);
    }
}

/// <summary>A real workspace with its Corkboard, beat board and plan host, for one UI Dispatch.</summary>
internal sealed class PlanTestContext : IDisposable
{
    private readonly OwnedDisposables _owned = new();
    private readonly IMetadataStore _store;
    private readonly InMemoryAppSettingsStore _settings = new();
    private readonly SwitchableFolderPicker _folders;
    private readonly ManuscriptService _manuscript;
    private readonly EditorViewModel _editor;
    private readonly PhaseDataService _phases;
    private readonly TargetsService _targets;
    private readonly BookTxtStructureService _structure;
    private readonly IDisposable _notificationLog;

    public NotificationService Notifications { get; } = new();
    public List<Notification> Messages { get; } = new();
    public IEnumerable<string> Infos => Messages.Where(m => m.Kind == NotificationKind.Info).Select(m => m.Message);
    public IEnumerable<string> Errors => Messages.Where(m => m.Kind == NotificationKind.Error).Select(m => m.Message);
    public HistoricalScheduler Scheduler { get; } = new();
    public ProjectSettingsService ProjectSettings { get; }
    public IBeatBoardService Boards { get; }
    public WorkspaceViewModel Workspace { get; }
    public EditorViewModel Editor => _editor;
    public CorkboardViewModel Corkboard { get; }
    public BeatBoardViewModel Board { get; }
    public StorageMigrator Migrator { get; }

    /// <param name="store">Every write of the context's services goes through it (default: the real store).</param>
    public PlanTestContext(string folder, Func<IMetadataStore, IBeatBoardService>? boards = null,
        IMetadataStore? store = null)
    {
        _store = store ?? new MetadataStore();
        _folders = new SwitchableFolderPicker(folder);
        _notificationLog = Notifications.Notifications.Subscribe(Messages.Add);
        var exclusions = new ExclusionManifestService(_store);
        var registry = new ChapterRegistryService(_store);
        var wordCount = new WordCountService();
        _phases = new PhaseDataService(_store);
        _targets = new TargetsService(_store);
        var storyWriter = new StoryFileWriter(_store);
        _structure = new BookTxtStructureService(_store, exclusions, registry, storyWriter);
        ProjectSettings = new ProjectSettingsService(_store);
        Boards = boards?.Invoke(_store) ?? new BeatBoardService(_store);
        var notes = new NotesService(_store);
        Migrator = new StorageMigrator(_phases, _targets, notes, Boards, storyWriter);
        _editor = _owned.Track(new EditorViewModel(_store, Notifications, wordCount, storyWriter));
        // As in the app: metadata changes for the chapter open in the editor are saved through it.
        storyWriter.AttachBuffer(_editor);
        _manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));

        Workspace = new WorkspaceViewModel(
            _manuscript,
            _structure,
            new StubFilePickerService(),
            _settings,
            _folders,
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
            new NeverInitPrompt(),
            notes,
            Boards);

        Corkboard = _owned.Track(new CorkboardViewModel(
            Workspace, _structure, new OrphanFileDiscoveryService(), _settings, Notifications, _manuscript));
        Board = _owned.Track(new BeatBoardViewModel(Workspace, Notifications, Scheduler));
    }

    public void Dispose()
    {
        _owned.Dispose();
        _notificationLog.Dispose();
    }

    public PlanHostViewModel CreateHost() =>
        _owned.Track(new PlanHostViewModel(Workspace, Corkboard, Board, Notifications, Boards));

    public SettingsViewModel CreateSettings(Func<Task<bool>>? flushStoryEdits = null) =>
        new(new AiTestDoubles.InMemoryProviderProfileService(),
            new AiTestDoubles.InMemoryCredentialStore(),
            new AiTestDoubles.NoOpAiChatService(),
            Notifications,
            Workspace,
            ProjectSettings,
            Boards,
            Board,
            Migrator,
            flushStoryEdits);

    /// <summary>
    /// Write mode's Beats panel with its own board (saves debounced on <see cref="Scheduler"/>), linked to
    /// <paramref name="planBoard"/> (default: <see cref="Board"/>) so each re-reads what the other saves.
    /// </summary>
    public WriteBeatsViewModel CreateWriteBeats(BeatBoardViewModel? planBoard = null)
    {
        var board = _owned.Track(new BeatBoardViewModel(Workspace, Notifications, Scheduler));
        return _owned.Track(new WriteBeatsViewModel(Workspace, board, planBoard ?? Board, _settings, Notifications));
    }

    public MainWindowViewModel CreateMainWindow(PlanHostViewModel host, WriteBeatsViewModel? writeBeats = null)
    {
        var docs = new SupplementalDocsViewModel(
            Workspace, new SupplementalDocsService(_store), _editor, Notifications, _settings, new StubFilePickerService());
        var history = new WordCountHistoryService(_store);

        return new MainWindowViewModel(
            Workspace,
            _editor,
            _owned.Track(new NotesViewModel(_editor, Workspace, Notifications, _settings)),
            _owned.Track(new ChapterInfoViewModel(_editor, Workspace, _settings, Notifications, history)),
            new BookStatsViewModel(Workspace, history),
            new GanttViewModel(Workspace, Notifications),
            Corkboard,
            new ResearchViewModel(Workspace, docs, _editor, AiTestDoubles.CreateStubAiChatViewModel(_editor, Workspace, Notifications)),
            docs,
            _owned.Track(new GitPanelViewModel(Workspace, _editor, new NoOpGitService(), Notifications)),
            Notifications,
            _settings,
            AiTestDoubles.CreateStubAiChatViewModel(_editor, Workspace, Notifications),
            AiTestDoubles.CreateStubSettingsFactory(Notifications),
            ideasViewModel: null,
            planHostViewModel: host,
            writeBeatsViewModel: writeBeats);
    }

    public NotesViewModel CreateNotes() =>
        _owned.Track(new NotesViewModel(_editor, Workspace, Notifications, _settings));

    public ChapterInfoViewModel CreateChapterInfo() =>
        _owned.Track(new ChapterInfoViewModel(_editor, Workspace, _settings, Notifications, new WordCountHistoryService(_store)));

    public GanttViewModel CreateGantt() => new(Workspace, Notifications);

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
        await PlanTestHarness.PumpUntilAsync(() => Corkboard.Items.OfType<ChapterCardItemViewModel>().Any(i => i.RelativePath == relativePath));
        return Corkboard.Items.OfType<ChapterCardItemViewModel>().First(i => i.RelativePath == relativePath);
    }

    public async Task<PartDividerItemViewModel> PartDividerAsync(string relativePath)
    {
        await PlanTestHarness.PumpUntilAsync(() => Corkboard.Items.OfType<PartDividerItemViewModel>().Any(i => i.RelativePath == relativePath));
        return Corkboard.Items.OfType<PartDividerItemViewModel>().First(i => i.RelativePath == relativePath);
    }

    /// <summary>Opens <paramref name="folder"/> (default: the context's folder) as the workspace, in this Dispatch.</summary>
    public async Task OpenWorkspaceInCurrentDispatchAsync(string? folder = null)
    {
        if (folder != null)
            _folders.Folder = folder;
        var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
        await PlanTestHarness.Within((Task)method.Invoke(Workspace, Array.Empty<object>())!);
        if (typeof(WorkspaceViewModel)
                .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Workspace) is Task hydration)
            await PlanTestHarness.Within(hydration);
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

    private sealed class SwitchableFolderPicker(string folder) : IFolderPickerService
    {
        public string Folder { get; set; } = folder;
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(Folder);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}
