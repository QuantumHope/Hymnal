using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class SettingsProjectSectionTests : IDisposable
{
    /// <summary>Contexts created by the running test; their file watchers are released in <see cref="Dispose"/>.</summary>
    private static readonly List<Ctx> LiveContexts = new();

    public void Dispose()
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
    }

    static SettingsProjectSectionTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task No_workspace_hides_project_section()
    {
        await Run(async () =>
        {
            using var ctx = new Ctx(null);
            Assert.False(ctx.Settings.HasWorkspace);
            Assert.False(ctx.Workspace.HasWorkspace);
            return 0;
        });
    }

    [Fact]
    public async Task Initial_selection_is_current_kind()
    {
        var root = CreateFolder(".hymnal-data/project.json", StoryJson);
        try
        {
            using var ctx = new Ctx(root);
            await ctx.OpenAsync();
            await Run(() =>
            {
                var settings = ctx.NewSettings();
                Assert.True(settings.HasWorkspace);
                Assert.Equal(ProjectKind.Story, settings.SelectedProjectKind);
                Assert.Equal(3, settings.ProjectCards.Count);
                Assert.Null(settings.ProjectChangeSummary);
                return Task.FromResult(0);
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Changing_selection_shows_summary()
    {
        var root = CreateFolder();
        try
        {
            using var ctx = new Ctx(root);
            await ctx.OpenAsync();
            await Run(() =>
            {
                var settings = ctx.NewSettings();
                settings.SelectedProjectKind = ProjectKind.Story;
                Assert.Equal(ProjectTypeCatalog.SwitchSummary(ProjectKind.Story), settings.ProjectChangeSummary);
                Assert.Equal("Status labels and Corkboard will switch to short story tools. No data is lost.",
                    settings.ProjectChangeSummary);
                settings.SelectedProjectKind = ProjectKind.Novel;
                Assert.Null(settings.ProjectChangeSummary);
                return Task.FromResult(0);
            });
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Apply_saves_and_reloads()
    {
        var root = CreateFolder(".hymnal-data/project.json",
            "{ \"schemaVersion\": 1, \"kind\": \"novel\", \"storage\": \"embedded\" }");
        try
        {
            using var ctx = new Ctx(root);
            await ctx.OpenAsync();
            await Run(async () =>
            {
                var settings = ctx.NewSettings();
                settings.SelectedProjectKind = ProjectKind.Story;
                await settings.ApplyProjectKindCommand.Execute().ToTask();
                return 0;
            });

            Assert.Equal(ProjectKind.Story, ctx.Workspace.Profile.Kind);
            var json = await File.ReadAllTextAsync(ProjectSettingsService.ProjectFilePath(root));
            Assert.Contains("\"story\"", json);
            Assert.Contains("\"embedded\"", json);
            Assert.Empty(ctx.Notifications.Errors);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Apply_disabled_when_unchanged()
    {
        var root = CreateFolder();
        try
        {
            using var ctx = new Ctx(root);
            await ctx.OpenAsync();
            await Run(() =>
            {
                var settings = ctx.NewSettings();
                var canExecute = false;
                using var sub = settings.ApplyProjectKindCommand.CanExecute
                    .Subscribe(v => canExecute = v);
                Assert.False(canExecute);
                settings.SelectedProjectKind = ProjectKind.Anthology;
                Assert.True(canExecute);
                settings.SelectedProjectKind = ProjectKind.Novel;
                Assert.False(canExecute);
                return Task.FromResult(0);
            });
        }
        finally { Delete(root); }
    }

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    private static Task<T> Run<T>(Func<Task<T>> action) => ReactiveUiTestBootstrap.RunOnUiThreadAsync(action);

    private static string CreateFolder(string? extraPath = null, string? extraContent = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-settings-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Book.txt"), "chapter-one.md\n");
        File.WriteAllText(Path.Combine(root, "chapter-one.md"), "# One\n");
        if (extraPath is not null)
        {
            var full = Path.Combine(root, extraPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, extraContent);
        }
        return root;
    }

    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class Ctx : IDisposable
    {
        private readonly ManuscriptService _manuscript;

        private readonly OwnedDisposables _owned = new();

        public void Dispose() => _owned.Dispose();

        public RecordingNotifications Notifications { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public WorkspaceViewModel Workspace { get; }
        public SettingsViewModel Settings { get; }
        private readonly StorageMigrator _migrator;

        public Ctx(string? folder)
        {
            var store = new MetadataStore();
            var exclusions = new ExclusionManifestService(store);
            var registry = new ChapterRegistryService(store);
            var wordCount = new WordCountService();
            ProjectSettings = new ProjectSettingsService(store);
            var writer = new StoryFileWriter(store);
            var editor = _owned.Track(new EditorViewModel(store, Notifications, wordCount, writer));
            _migrator = new StorageMigrator(
                new PhaseDataService(store), new TargetsService(store), new NotesService(store),
                new BeatBoardService(store), writer);
            _manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));
            LiveContexts.Add(this);
            Workspace = new WorkspaceViewModel(
                _manuscript,
                new BookTxtStructureService(store, exclusions, registry),
                new StubFilePickerService(),
                new MemorySettings(),
                new FixedFolder(folder),
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
                new NoPrompt());
            Settings = NewSettings();
        }

        public SettingsViewModel NewSettings() => new(
            new AiTestDoubles.InMemoryProviderProfileService(),
            new AiTestDoubles.InMemoryCredentialStore(),
            new AiTestDoubles.NoOpAiChatService(),
            Notifications,
            Workspace,
            ProjectSettings,
            storageMigrator: _migrator);

        public Task OpenAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return Run(async () =>
            {
                await (Task)method.Invoke(Workspace, Array.Empty<object>())!;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline && Workspace.Nodes.Count != 1)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    await Task.Delay(25);
                }
                Assert.Equal(1, Workspace.Nodes.Count);
                return 0;
            });
        }
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<string> Errors { get; } = new();
        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message) { }
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class MemorySettings : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new();
        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var v) && v is T t ? t : default);
        public Task SetAsync<T>(string key, T value) { _values[key] = value; return Task.CompletedTask; }
    }

    private sealed class FixedFolder(string? folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult(folder);
    }

    private sealed class NoPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }
}
