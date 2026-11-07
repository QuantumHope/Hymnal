using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
public sealed class WorkspaceProjectKindTests
{
    static WorkspaceProjectKindTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryProjectJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    [Fact]
    public async Task Open_existing_workspace_without_project_json_uses_novel()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            var context = new TestContext(root);
            await context.OpenAsync();
            await WaitForNodesAsync(context, 1);

            Assert.True(context.Workspace.HasWorkspace);
            Assert.Same(ProjectProfile.Novel, context.Workspace.Profile);
            Assert.All(context.Workspace.Nodes, n => Assert.Equal(ProjectKind.Novel, n.Profile.Kind));
            Assert.Equal(0, context.Prompt.ConfirmCalls);
            Assert.Equal(0, context.Prompt.ChooseCalls);
            Assert.Empty(context.Notifications.Infos);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Open_story_workspace_sets_profile_and_chapter_profiles()
    {
        var root = CreateFolder(
            ("Book.txt", "scene-one.md\nscene-two.md\n"),
            ("scene-one.md", "# Scene One\n"),
            ("scene-two.md", "# Scene Two\n"),
            (".hymnal-data/project.json", StoryProjectJson));
        try
        {
            var context = new TestContext(root);
            await context.OpenAsync();
            await WaitForNodesAsync(context, 2);

            Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
            Assert.All(context.Workspace.Nodes, n => Assert.Equal(ProjectKind.Story, n.Profile.Kind));
            var first = context.Workspace.Nodes.First();
            Assert.Null(first.PhaseData);
            Assert.Equal(ChapterStatus.Planned, first.Status);
            Assert.Equal("Idea", first.StatusLabel);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Open_with_malformed_project_json_warns_and_uses_novel()
    {
        var root = CreateFolder(
            ("Book.txt", "chapter-one.md\n"),
            ("chapter-one.md", "# One\n"),
            (".hymnal-data/project.json", "{ this is not json"));
        var projectPath = ProjectSettingsService.ProjectFilePath(root);
        var before = await File.ReadAllBytesAsync(projectPath);
        try
        {
            var context = new TestContext(root);
            await context.OpenAsync();
            await WaitForNodesAsync(context, 1);

            Assert.Same(ProjectProfile.Novel, context.Workspace.Profile);
            var info = Assert.Single(context.Notifications.Infos);
            Assert.Contains("Opening as a novel", info);
            Assert.Equal(before, await File.ReadAllBytesAsync(projectPath));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Open_uninitialized_folder_confirm_and_choose_story_initializes_and_opens()
    {
        var root = CreateFolder();
        try
        {
            var context = new TestContext(root);
            context.Prompt.ConfirmResult = true;
            context.Prompt.ChooseResult = ProjectKind.Story;

            await context.OpenAsync();
            await WaitForNodesAsync(context, 1);

            Assert.True(context.Workspace.HasWorkspace);
            Assert.True(File.Exists(Path.Combine(root, "Book.txt")));
            Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
            Assert.Equal(1, context.Prompt.ConfirmCalls);
            Assert.Equal(root, context.Prompt.LastConfirmedPath);
            Assert.Equal(1, context.Prompt.ChooseCalls);
            Assert.Empty(context.Notifications.Errors);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Decline_initialize_writes_nothing()
    {
        var root = CreateFolder(("notes.md", "# Notes\n"));
        var before = Snapshot(root);
        try
        {
            var context = new TestContext(root);
            context.Prompt.ConfirmResult = false;

            await context.OpenAsync();

            Assert.Equal(before, Snapshot(root));
            Assert.False(context.Workspace.HasWorkspace);
            Assert.Empty(context.Notifications.Errors);
            Assert.Equal(1, context.Prompt.ConfirmCalls);
            Assert.Equal(0, context.Prompt.ChooseCalls);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Cancel_type_choice_writes_nothing()
    {
        var root = CreateFolder(("notes.md", "# Notes\n"));
        var before = Snapshot(root);
        try
        {
            var context = new TestContext(root);
            context.Prompt.ConfirmResult = true;
            context.Prompt.ChooseResult = null;

            await context.OpenAsync();

            Assert.Equal(before, Snapshot(root));
            Assert.False(context.Workspace.HasWorkspace);
            Assert.Empty(context.Notifications.Errors);
            Assert.Equal(1, context.Prompt.ChooseCalls);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Reload_project_picks_up_new_kind()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            var context = new TestContext(root);
            await context.OpenAsync();
            await WaitForNodesAsync(context, 1);
            Assert.Equal(ProjectKind.Novel, context.Workspace.Profile.Kind);

            var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Story));
            Assert.True(save.IsSuccess, save.Error);

            var reload = await RunOnUiThreadWithPumpingAsync(() => context.Workspace.ReloadProjectAsync());
            Assert.True(reload.IsSuccess, reload.Error);

            Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
            await WaitUntilAsync(
                () => context.Workspace.Nodes.Count == 1 && context.Workspace.Nodes.All(n => n.Profile.Kind == ProjectKind.Story),
                () => $"Chapter view models were not rebuilt with the story profile. Errors: {string.Join(" | ", context.Notifications.Errors)}");
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_complete_phase_from_drafting_goes_to_editing()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var phaseService = new PhaseDataService(store);
            var storyProfile = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));
            var vm = new ChapterViewModel(
                new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0),
                "uuid-1",
                new PhaseData { Status = ChapterStatus.Drafting },
                phaseService,
                new TargetsService(store),
                new InMemoryAppSettingsStore(),
                new RecordingNotificationService(),
                root,
                target: null,
                profile: storyProfile);

            Assert.Same(storyProfile, vm.Profile);

            await RunOnUiThreadWithPumpingAsync(async () => { await vm.CompleteCurrentPhaseAsync(); return 0; });
            await WaitUntilAsync(() => vm.Status == ChapterStatus.Editing, $"Expected Editing, got {vm.Status}.");
            Assert.Equal("Revising", vm.StatusLabel);

            // Story profile skips Polishing/Reviewing: Editing advances straight to Done.
            await RunOnUiThreadWithPumpingAsync(async () => { await vm.CompleteCurrentPhaseAsync(); return 0; });
            await WaitUntilAsync(() => vm.Status == ChapterStatus.Done, $"Expected Done, got {vm.Status}.");
            Assert.Equal("Done", vm.StatusLabel);

            var phases = await phaseService.LoadAsync(root);
            Assert.Equal(ChapterStatus.Done, phases["uuid-1"].Status);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateFolder(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-project-kind-{Guid.NewGuid():N}");
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

    private static string Snapshot(string root) =>
        string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p) + (File.Exists(p) ? ":" + File.ReadAllText(p) : "/"))
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Runs <paramref name="action"/> on the headless UI thread, keeping its dispatcher loop alive until done.</summary>
    private static async Task<T> RunOnUiThreadWithPumpingAsync<T>(Func<Task<T>> action)
    {
        var task = ReactiveUiTestBootstrap.RunOnUiThreadAsync(action);
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(40))) != task)
            Assert.Fail("UI-thread operation did not complete in time.");
        return await task;
    }

    private static Task WaitForNodesAsync(TestContext context, int expectedCount) =>
        WaitUntilAsync(() => context.Workspace.Nodes.Count == expectedCount,
            () => $"Workspace nodes did not reach {expectedCount}; actual {context.Workspace.Nodes.Count}. " +
                  $"Errors: {string.Join(" | ", context.Notifications.Errors)}; " +
                  $"hydration={(typeof(WorkspaceViewModel).GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context.Workspace) as Task)?.Status.ToString() ?? "null"}; " +
                  $"hasWorkspace={context.Workspace.HasWorkspace}");

    /// <summary>Polls on the UI thread so background <c>Dispatcher.UIThread.InvokeAsync</c> work can run.</summary>
    private static Task WaitUntilAsync(Func<bool> predicate, string failureMessage) =>
        WaitUntilAsync(predicate, () => failureMessage);

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
        public RecordingNotificationService Notifications { get; } = new();
        public FakeProjectInitPrompt Prompt { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var store = new MetadataStore();
            var exclusions = new ExclusionManifestService(store);
            var registry = new ChapterRegistryService(store);
            var wordCount = new WordCountService();
            ProjectSettings = new ProjectSettingsService(store);
            var editor = new EditorViewModel(store, Notifications, wordCount);

            Workspace = new WorkspaceViewModel(
                new ManuscriptService(Notifications, exclusions),
                new BookTxtStructureService(store, exclusions, registry),
                new StubFilePickerService(),
                new InMemoryAppSettingsStore(),
                new FixedFolderPickerService(folder),
                Notifications,
                editor,
                registry,
                new PhaseDataService(store),
                new TargetsService(store),
                wordCount,
                new WordCountHistoryService(store),
                exclusions,
                new OrphanFileDiscoveryService(),
                ProjectSettings,
                new ProjectInitializer(store, ProjectSettings),
                Prompt);
        }

        public Task OpenAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
            return RunOnUiThreadWithPumpingAsync(async () =>
            {
                await (Task)method.Invoke(Workspace, Array.Empty<object>())!;
                return 0;
            });
        }
    }

    private sealed class FakeProjectInitPrompt : IProjectInitPrompt
    {
        public bool ConfirmResult { get; set; }
        public ProjectKind? ChooseResult { get; set; }
        public int ConfirmCalls { get; private set; }
        public int ChooseCalls { get; private set; }
        public string? LastConfirmedPath { get; private set; }

        public Task<bool> ConfirmInitializeAsync(string folderPath)
        {
            ConfirmCalls++;
            LastConfirmedPath = folderPath;
            return Task.FromResult(ConfirmResult);
        }

        public Task<ProjectKind?> ChooseProjectKindAsync()
        {
            ChooseCalls++;
            return Task.FromResult(ChooseResult);
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public List<string> Successes { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) => Successes.Add(message);
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
