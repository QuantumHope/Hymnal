using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>
/// Statuses, Gantt phases and labels shown in the UI come from the workspace's
/// <see cref="ProjectProfile"/>; Novel keeps the original lists.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class ProfileDrivenUiTests
{
    static ProfileDrivenUiTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryProjectJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    [Fact]
    public async Task Story_chapter_info_status_options_are_four()
    {
        var root = CreateStoryFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 2);
            var info = context.CreateChapterInfo();

            Assert.Equal(["Idea", "Drafting", "Revising", "Done"], info.StatusOptions.Select(o => o.Label));
            Assert.Equal(
                [ChapterStatus.Planned, ChapterStatus.Drafting, ChapterStatus.Editing, ChapterStatus.Done],
                info.StatusOptions.Select(o => o.Value));
            Assert.Equal(["Drafting", "Editing"], info.PhaseScheduleRows.Select(r => r.PhaseName));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_chapter_info_status_options_are_seven()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            var context = await OpenAsync(root, expectedNodes: 1);
            var info = context.CreateChapterInfo();

            Assert.Equal(
                Enum.GetValues<ChapterStatus>().Select(s => s.ToString()),
                info.StatusOptions.Select(o => o.Label));
            Assert.Equal(Enum.GetValues<ChapterStatus>(), info.StatusOptions.Select(o => o.Value));
            Assert.Equal(GanttProjection.PhaseNames, info.PhaseScheduleRows.Select(r => r.PhaseName));
            Assert.Equal("Chapter Info", info.PanelTitle);
            Assert.Equal("Toggle _Chapter Info", info.ToggleMenuHeader);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_info_panel_title_is_story_info()
    {
        var root = CreateStoryFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 2);
            var info = context.CreateChapterInfo();

            Assert.Equal("Story Info", info.PanelTitle);
            Assert.Equal("Toggle _Story Info", info.ToggleMenuHeader);
            Assert.Equal("Story Info (F3)", info.ToggleToolTip);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_gantt_rows_have_two_phase_segments()
    {
        var root = CreateStoryFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 2);
            var gantt = new GanttViewModel(context.Workspace, context.PhaseService, context.Notifications);

            var chapterRows = gantt.Rows.Where(r => r.IsChapter).ToList();
            Assert.Equal(2, chapterRows.Count);
            Assert.All(chapterRows, row =>
            {
                Assert.Equal(["Drafting", "Editing"], row.PhaseSegments.Select(s => s.PhaseName));
                Assert.Equal(["Idea", "Drafting", "Revising", "Done"], row.AvailableStatuses.Select(o => o.Label));
            });
            Assert.Equal(["Idea", "Drafting", "Revising", "Done"], gantt.AvailableStatuses.Select(o => o.Label));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Story_gantt_row_keeps_stored_status_when_display_bucket_is_reselected()
    {
        var profile = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));
        var node = new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0);
        var row = new GanttRowViewModel(
            GanttProjection.Project(node, new PhaseData { Status = ChapterStatus.Polishing }, profile), profile);

        Assert.Equal(ChapterStatus.Editing, row.EditableDisplayStatus);
        Assert.Equal("Revising", row.StatusDisplay);

        row.EditableDisplayStatus = ChapterStatus.Editing;
        Assert.Equal(ChapterStatus.Polishing, row.EditableStatus);

        row.EditableDisplayStatus = ChapterStatus.Done;
        Assert.Equal(ChapterStatus.Done, row.EditableStatus);
    }

    [Fact]
    public void Story_card_status_display_maps_polishing_to_revising()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var chapter = new ChapterViewModel(
                new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0),
                "uuid-1",
                new PhaseData { Status = ChapterStatus.Polishing },
                new PhaseDataService(store),
                new TargetsService(store),
                new InMemoryAppSettingsStore(),
                new RecordingNotificationService(),
                root,
                target: null,
                profile: ProjectProfile.For(new ProjectSettings(ProjectKind.Story)));

            var card = new CardViewModel(chapter);

            Assert.Equal(ChapterStatus.Polishing, card.Status);
            Assert.Equal("Revising", card.StatusDisplay);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_book_status_summary_buckets_by_display_status()
    {
        var root = CreateStoryFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 2);
            var nodes = context.Workspace.Nodes.ToList();

            await RunOnUiThreadWithPumpingAsync(() =>
            {
                nodes[0].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Polishing });
                nodes[1].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Editing });
                return Task.FromResult(0);
            });

            await WaitUntilAsync(
                () => context.Workspace.BookStatusSummary.Count == 1,
                () => "Expected a single status bucket; got " +
                      string.Join(", ", context.Workspace.BookStatusSummary.Select(s => $"{s.Status}={s.Count}")));

            var bucket = Assert.Single(context.Workspace.BookStatusSummary);
            Assert.Equal(ChapterStatus.Editing, bucket.Status);
            Assert.Equal(2, bucket.Count);

            // Display mapping never rewrites the stored status.
            Assert.Equal(ChapterStatus.Polishing, nodes[0].Status);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateStoryFolder() => CreateFolder(
        ("Book.txt", "scene-one.md\nscene-two.md\n"),
        ("scene-one.md", "# Scene One\n"),
        ("scene-two.md", "# Scene Two\n"),
        (".hymnal-data/project.json", StoryProjectJson));

    private static string CreateFolder(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-profile-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task<TestContext> OpenAsync(string root, int expectedNodes)
    {
        var context = new TestContext(root);
        var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
        await RunOnUiThreadWithPumpingAsync(async () =>
        {
            await (Task)method.Invoke(context.Workspace, Array.Empty<object>())!;
            return 0;
        });
        await WaitUntilAsync(
            () => context.Workspace.Nodes.Count == expectedNodes,
            () => $"Workspace nodes did not reach {expectedNodes}; actual {context.Workspace.Nodes.Count}. " +
                  $"Errors: {string.Join(" | ", context.Notifications.Errors)}");
        return context;
    }

    /// <summary>Runs <paramref name="action"/> on the headless UI thread, keeping its dispatcher loop alive until done.</summary>
    private static async Task<T> RunOnUiThreadWithPumpingAsync<T>(Func<Task<T>> action)
    {
        var task = ReactiveUiTestBootstrap.RunOnUiThreadAsync(action);
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40))) != task)
            Assert.Fail("UI-thread operation did not complete in time.");
        return await task;
    }

    /// <summary>Polls on the UI thread so background <c>Dispatcher.UIThread.InvokeAsync</c> work can run.</summary>
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

    private sealed class TestContext
    {
        private readonly MetadataStore _store = new();
        private readonly InMemoryAppSettingsStore _settings = new();
        private readonly EditorViewModel _editor;

        public RecordingNotificationService Notifications { get; } = new();
        public PhaseDataService PhaseService { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var exclusions = new ExclusionManifestService(_store);
            var registry = new ChapterRegistryService(_store);
            var wordCount = new WordCountService();
            var projectSettings = new ProjectSettingsService(_store);
            PhaseService = new PhaseDataService(_store);
            _editor = new EditorViewModel(_store, Notifications, wordCount);

            Workspace = new WorkspaceViewModel(
                new ManuscriptService(Notifications, exclusions),
                new BookTxtStructureService(_store, exclusions, registry),
                new StubFilePickerService(),
                _settings,
                new FixedFolderPickerService(folder),
                Notifications,
                _editor,
                registry,
                PhaseService,
                new TargetsService(_store),
                wordCount,
                new WordCountHistoryService(_store),
                exclusions,
                new OrphanFileDiscoveryService(),
                projectSettings,
                new ProjectInitializer(_store, projectSettings),
                new DecliningProjectInitPrompt());
        }

        public ChapterInfoViewModel CreateChapterInfo() => new(
            _editor,
            Workspace,
            PhaseService,
            new TargetsService(_store),
            _settings,
            Notifications,
            new WordCountHistoryService(_store));
    }

    private sealed class DecliningProjectInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) { }
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
