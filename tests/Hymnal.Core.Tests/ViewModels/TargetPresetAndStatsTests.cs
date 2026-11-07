using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>Story-length target presets, Part targets for anthologies and collection stats titles.</summary>
[Collection("AvaloniaUi")]
public sealed class TargetPresetAndStatsTests
{
    static TargetPresetAndStatsTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    [Fact]
    public void Story_chapter_has_five_presets()
    {
        var root = CreateFolder();
        try
        {
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, new TargetsService(new MetadataStore()));
            Assert.True(chapter.HasTargetPresets);
            Assert.Equal(["flash", "shortStory", "novelette", "novella", "custom"], chapter.TargetPresets.Select(p => p.Key));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Novel_chapter_has_no_presets()
    {
        var root = CreateFolder();
        try
        {
            var chapter = CreateChapter(root, ProjectKind.Novel, NodeKind.Chapter, new TargetsService(new MetadataStore()));
            Assert.False(chapter.HasTargetPresets);
            Assert.Empty(chapter.TargetPresets);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Selecting_preset_fills_pending_min_max()
    {
        var root = CreateFolder();
        try
        {
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, new TargetsService(new MetadataStore()));
            chapter.PendingPreset = chapter.TargetPresets.Single(p => p.Key == "novelette");
            Assert.Equal(7500, chapter.PendingMinWords);
            Assert.Equal(17500, chapter.PendingMaxWords);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Confirm_with_preset_saves_preset_key()
    {
        var root = CreateFolder();
        try
        {
            var targets = new TargetsService(new MetadataStore());
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, targets);
            await ConfirmAsync(chapter, c => c.PendingPreset = c.TargetPresets.Single(p => p.Key == "novelette"));

            var saved = (await targets.LoadAsync(root))["uuid-1"];
            Assert.Equal("novelette", saved.Preset);
            Assert.Equal(7500, saved.MinWords);
            Assert.Equal(17500, saved.MaxWords);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Editing_min_after_preset_saves_custom()
    {
        var root = CreateFolder();
        try
        {
            var targets = new TargetsService(new MetadataStore());
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, targets);
            await ConfirmAsync(chapter, c =>
            {
                c.PendingPreset = c.TargetPresets.Single(p => p.Key == "novelette");
                c.PendingMinWords = 8000;
            });

            var saved = (await targets.LoadAsync(root))["uuid-1"];
            Assert.Equal("custom", saved.Preset);
            Assert.Equal(8000, saved.MinWords);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Target_display_prefixes_preset_label()
    {
        var root = CreateFolder();
        try
        {
            var target = new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" };
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, new TargetsService(new MetadataStore()), target);

            var card = new CardViewModel(chapter);

            Assert.StartsWith("Short Story · ", card.TargetDisplay);
            Assert.Contains("1,000–7,500 w", card.TargetDisplay);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Custom_preset_does_not_prefix_target_display()
    {
        var root = CreateFolder();
        try
        {
            var target = new WordCountTarget { MinWords = 2000, MaxWords = 3000, Preset = "custom" };
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, new TargetsService(new MetadataStore()), target);

            Assert.Equal("2,000–3,000 w", new CardViewModel(chapter).TargetDisplay);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Anthology_part_proximity_uses_part_total()
    {
        var root = CreateFolder();
        try
        {
            var target = new WordCountTarget { MinWords = 1000, MaxWords = 7500 };
            var part = CreateChapter(root, ProjectKind.Anthology, NodeKind.Part, new TargetsService(new MetadataStore()), target);

            part.PartTotalWordCount = 2000 + 1750;

            Assert.Equal(3750, part.EffectiveWordCount);
            Assert.Equal(0.5, part.ProximityFill, 3);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_allows_part_target_flyout()
    {
        var root = CreateAnthologyFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 3);
            var part = context.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Part);

            Assert.True(part.CanSetTarget);
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await part.OpenTargetFlyoutCommand.Execute();
                return 0;
            });
            Assert.True(part.IsTargetFlyoutOpen);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_does_not_open_part_target_flyout()
    {
        var root = CreateFolder(
            ("Book.txt", "part-one.md\nchapter-a.md\n"),
            ("part-one.md", "{class: part}\n# Part One\n"),
            ("chapter-a.md", "# A\n"));
        try
        {
            var context = await OpenAsync(root, expectedNodes: 2);
            var part = context.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Part);

            Assert.False(part.CanSetTarget);
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await part.OpenTargetFlyoutCommand.Execute();
                return 0;
            });
            Assert.False(part.IsTargetFlyoutOpen);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_hides_book_stats()
    {
        var root = CreateFolder(
            ("Book.txt", "scene-one.md\n"), ("scene-one.md", "# One\n"), (".hymnal-data/project.json", StoryJson));
        try
        {
            var context = await OpenAsync(root, expectedNodes: 1);
            var stats = context.CreateStats();

            Assert.False(stats.IsVisible);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_book_stats_titles_are_unchanged()
    {
        var root = CreateFolder(("Book.txt", "chapter-a.md\n"), ("chapter-a.md", "# A\n"));
        try
        {
            var context = await OpenAsync(root, expectedNodes: 1);
            var stats = context.CreateStats();

            Assert.True(stats.IsVisible);
            Assert.Equal("BOOK STATS", stats.PanelTitle);
            Assert.Equal("BY PART", stats.BarSectionLabel);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_book_stats_titles()
    {
        var root = CreateAnthologyFolder();
        try
        {
            var context = await OpenAsync(root, expectedNodes: 3);
            var stats = context.CreateStats();

            Assert.True(stats.IsVisible);
            Assert.Equal("COLLECTION STATS", stats.PanelTitle);
            Assert.Equal("BY STORY", stats.BarSectionLabel);

            var part = context.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Part);
            await RunOnUiThreadWithPumpingAsync(() =>
            {
                context.Workspace.SelectedNode = part;
                return Task.FromResult(0);
            });

            Assert.True(stats.IsVisible);
            Assert.Equal("STORY STATS", stats.PanelTitle);
            Assert.Equal("BY SCENE", stats.BarSectionLabel);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Chapter_info_min_only_edit_keeps_existing_max()
    {
        var root = CreateFolder(
            ("Book.txt", "scene-one.md\n"), ("scene-one.md", "# One\n"), (".hymnal-data/project.json", StoryJson));
        try
        {
            var context = await OpenAsync(root, expectedNodes: 1);
            var node = context.Workspace.Nodes.First();
            await WaitUntilAsync(() => !string.IsNullOrEmpty(node.Uuid), () => "No UUID.");
            var info = await context.CreateChapterInfoForAsync(node);
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await node.SetTargetCommand.Execute(
                    new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" });
                await info.SetTargetCommand.Execute(2000);
                return 0;
            });

            var saved = (await new TargetsService(new MetadataStore()).LoadAsync(root))[node.Uuid];
            Assert.Equal(2000, saved.MinWords);
            Assert.Equal(7500, saved.MaxWords);
            Assert.Equal("custom", saved.Preset);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Confirm_without_edits_on_loaded_preset_target_leaves_it_unchanged()
    {
        var root = CreateFolder();
        try
        {
            var targets = new TargetsService(new MetadataStore());
            var stored = new WordCountTarget { MinWords = 7500, MaxWords = 17500, Preset = "novelette" };
            await targets.UpsertAsync(root, "uuid-1", stored);
            var chapter = CreateChapter(root, ProjectKind.Story, NodeKind.Chapter, targets, stored);

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await chapter.OpenTargetFlyoutCommand.Execute();
                await chapter.ConfirmTargetCommand.Execute();
                return 0;
            });

            var saved = (await targets.LoadAsync(root))["uuid-1"];
            Assert.Equal("novelette", saved.Preset);
            Assert.Equal(7500, saved.MinWords);
            Assert.Equal(17500, saved.MaxWords);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_part_target_persists_and_reloads()
    {
        var root = CreateAnthologyFolder();
        try
        {
            var first = await OpenAsync(root, expectedNodes: 3);
            var part = first.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Part);
            await WaitUntilAsync(() => !string.IsNullOrEmpty(part.Uuid), () => "No part UUID.");
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await part.SetTargetCommand.Execute(
                    new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" });
                return 0;
            });

            var second = await OpenAsync(root, expectedNodes: 3);
            var reloaded = second.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Part);
            await WaitUntilAsync(() => reloaded.Target is not null, () => "Part target did not reload.");
            Assert.Equal("shortStory", reloaded.Target!.Preset);
            Assert.Equal(7500, reloaded.Target.MaxWords);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateAnthologyFolder() => CreateFolder(
        ("Book.txt", "story-one.md\nscene-a.md\nscene-b.md\n"),
        ("story-one.md", "{class: part}\n# Story One\n"),
        ("scene-a.md", "# Scene A\n"),
        ("scene-b.md", "# Scene B\n"),
        (".hymnal-data/project.json", AnthologyJson));

    private static ChapterViewModel CreateChapter(
        string root, ProjectKind kind, NodeKind nodeKind, TargetsService targets, WordCountTarget? target = null)
    {
        var store = new MetadataStore();
        return new ChapterViewModel(
            new ChapterNode("node.md", "node.md", "Node", nodeKind, IsMissing: false, Index: 0),
            "uuid-1",
            phaseData: null,
            new PhaseDataService(store),
            targets,
            new InMemoryAppSettingsStore(),
            new RecordingNotificationService(),
            root,
            target,
            ProjectProfile.For(new ProjectSettings(kind)));
    }

    private static async Task ConfirmAsync(ChapterViewModel chapter, Action<ChapterViewModel> stage)
    {
        await RunOnUiThreadWithPumpingAsync(async () =>
        {
            stage(chapter);
            await chapter.ConfirmTargetCommand.Execute();
            return 0;
        });
    }

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

        public BookStatsViewModel CreateStats() =>
            new(Workspace, new WordCountHistoryService(_store), new TargetsService(_store));

        /// <summary>Creates a ChapterInfoViewModel with <paramref name="node"/> open in the editor.</summary>
        public async Task<ChapterInfoViewModel> CreateChapterInfoForAsync(ChapterViewModel node)
        {
            var info = CreateChapterInfo();
            var path = Path.Combine(Workspace.ManuscriptRoot, node.Node.RelativePath);
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await _editor.OpenChapterAsync(node.Node, path);
                return 0;
            });
            await WaitUntilAsync(() => info.ChapterTitle == node.Node.Title, () => "Chapter info did not load the chapter.");
            return info;
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
