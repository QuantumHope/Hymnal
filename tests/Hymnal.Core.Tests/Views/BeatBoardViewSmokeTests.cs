using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class BeatBoardViewSmokeTests
{
    static BeatBoardViewSmokeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    [Fact]
    public async Task BeatBoardView_shows_template_picker_when_empty_and_cards_after_applying()
    {
        var root = CreateStory();
        try
        {
            await Ui(root, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var view = new BeatBoardView { DataContext = context.Board };
                LayoutView(view);

                var picker = view.FindControl<ScrollViewer>("TemplatePickerPanel")!;
                var beats = view.FindControl<ListBox>("BeatList")!;
                Assert.True(picker.IsVisible);
                Assert.False(beats.IsVisible);
                Assert.Same(context.Board.TemplateOptions, view.FindControl<ItemsControl>("TemplatePicker")!.ItemsSource);
                Assert.Same(context.Board.Beats, beats.ItemsSource);

                await Within(context.Board.ApplyTemplateCommand
                    .Execute(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Append, null)).ToTask());
                LayoutView(view);

                Assert.False(picker.IsVisible);
                Assert.True(beats.IsVisible);
                Assert.Equal(5, beats.ItemCount);
                Assert.False(view.FindControl<Border>("ReadOnlyBanner")!.IsVisible);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task PlanHostView_shows_board_with_files_strip_and_hides_breadcrumb_for_a_story()
    {
        var root = CreateStory();
        try
        {
            await Ui(root, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var view = new PlanHostView { DataContext = host };
                LayoutView(view);

                Assert.Same(host.Content, view.FindControl<ContentControl>("PlanContent")!.Content);
                Assert.False(view.FindControl<Border>("Breadcrumb")!.IsVisible);
                var strip = view.FindControl<Expander>("FilesStrip")!;
                Assert.True(strip.IsVisible);
                Assert.False(strip.IsExpanded);

                host.IsFilesStripExpanded = true;
                LayoutView(view);
                Assert.True(strip.IsExpanded);
                Assert.Same(host.Corkboard, view.FindControl<ContentControl>("FilesContent")!.Content);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void BeatBoardView_Axaml_declares_editing_affordances()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/BeatBoardView.axaml"));

        Assert.Contains("x:Class=\"Hymnal.Views.BeatBoardView\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding Selected, Mode=TwoWay}\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding SceneChoices}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedScene, Mode=TwoWay}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding IsSceneMissing}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedParameter, Mode=TwoWay}\"", axaml);
        Assert.Contains("CommandParameter=\"{Binding AppendRequest}\"", axaml);
        Assert.Contains("CommandParameter=\"{Binding ReplaceRequest}\"", axaml);
        Assert.Contains("Command=\"{Binding AddBeatCommand}\"", axaml);
        Assert.Contains("Loaded=\"BeatCard_Loaded\"", axaml);
        Assert.Contains("Text=\"Apply template…\"", axaml);
    }

    [Fact]
    public void CorkboardView_Axaml_offers_beats_and_open_in_anthology()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/CorkboardView.axaml"));

        Assert.Contains("Command=\"{Binding DataContext.ShowBeatsCommand, ElementName=Root}\"", axaml);
        Assert.Contains("Command=\"{Binding DataContext.OpenInEditorCommand, ElementName=Root}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding DataContext.IsAnthology, ElementName=Root}\"", axaml);
        Assert.Contains("DoubleTapped=\"PartHeader_DoubleTapped\"", axaml);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private static void LayoutView(Control view)
    {
        view.ApplyTemplate();
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
    }

    private static string CreateStory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-beat-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), "scene-one.md\nscene-two.md\n");
        File.WriteAllText(Path.Combine(root, "scene-one.md"), "# Scene One\n\nDusk.\n");
        File.WriteAllText(Path.Combine(root, "scene-two.md"), "# Scene Two\n\nDark.\n");
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), StoryJson);
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }

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

    private static Task Ui(string root, Func<TestContext, Task> body) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var context = new TestContext(root);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == 2);
            await body(context);
            return 0;
        }, TimeSpan.FromSeconds(40));

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();

        public NotificationService Notifications { get; } = new();
        public WorkspaceViewModel Workspace { get; }
        public CorkboardViewModel Corkboard { get; }
        public BeatBoardViewModel Board { get; }

        public TestContext(string folder)
        {
            var store = new MetadataStore();
            var exclusions = new ExclusionManifestService(store);
            var registry = new ChapterRegistryService(store);
            var wordCount = new WordCountService();
            var projectSettings = new ProjectSettingsService(store);
            var structure = new BookTxtStructureService(store, exclusions, registry);
            var settings = new InMemoryAppSettingsStore();
            var editor = _owned.Track(new EditorViewModel(store, Notifications, wordCount));
            var manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));

            Workspace = new WorkspaceViewModel(
                manuscript, structure, new StubFilePickerService(), settings, new FixedFolderPickerService(folder),
                Notifications, editor, registry, new PhaseDataService(store), new TargetsService(store), wordCount,
                new WordCountHistoryService(store), exclusions, new OrphanFileDiscoveryService(), projectSettings,
                new ProjectInitializer(store, projectSettings), new NeverInitPrompt());

            Corkboard = _owned.Track(new CorkboardViewModel(
                Workspace, structure, new OrphanFileDiscoveryService(), settings, Notifications, manuscript));
            Board = _owned.Track(new BeatBoardViewModel(Workspace, new BeatBoardService(store), Notifications, new HistoricalScheduler()));
        }

        public void Dispose() => _owned.Dispose();

        public PlanHostViewModel CreateHost() => _owned.Track(new PlanHostViewModel(Workspace, Corkboard, Board, Notifications));

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
