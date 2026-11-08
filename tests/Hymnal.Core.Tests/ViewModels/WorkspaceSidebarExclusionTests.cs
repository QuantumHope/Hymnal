using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using ReactiveUI;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class WorkspaceSidebarExclusionTests
{
    static WorkspaceSidebarExclusionTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task LoadWorkspaceAsync_ProjectsManifestExcludedChaptersInSidebarOrderAndIgnoresOrdinaryOrphans()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-01.md\npart-one/chapter-03.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-01.md", "# Chapter One"),
            ("part-one/chapter-02.md", "# Chapter Two"),
            ("part-one/chapter-03.md", "# Chapter Three"),
            ("part-one/unmanifested-orphan.md", "# Orphan"));

        try
        {
            using var context = new TestContext(workspace.Root);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "part-one/chapter-02.md");
            Assert.True(excluded.IsSuccess, excluded.Error);

            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 4);

            Assert.Equal(
                new[]
                {
                    "part-one/part.md",
                    "part-one/chapter-01.md",
                    "part-one/chapter-03.md",
                    "part-one/chapter-02.md"
                },
                context.Workspace.Nodes.Select(node => node.Node.RelativePath));

            var excludedNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "part-one/chapter-02.md");
            Assert.True(excludedNode.Node.IsExcluded);
            Assert.False(context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-one/chapter-01.md").Node.IsExcluded);
            Assert.DoesNotContain(context.Workspace.Nodes, node => node.Node.RelativePath == "part-one/unmanifested-orphan.md");

            Assert.Equal(
                context.Workspace.Nodes.Select(node => node.Node.RelativePath),
                context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task LoadWorkspaceAsync_ReloadingFreshWorkspaceViewModel_ReprojectsManifestExcludedNodes()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            using var firstContext = new TestContext(workspace.Root);
            var excluded = await firstContext.ExclusionManifestService.ExcludeAsync(workspace.Root, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await firstContext.OpenWorkspaceAsync();
            await WaitForNodesAsync(firstContext.Workspace, 2);
            Assert.Contains(firstContext.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md" && node.Node.IsExcluded);

            using var secondContext = new TestContext(workspace.Root);
            await secondContext.OpenWorkspaceAsync();
            await WaitForNodesAsync(secondContext.Workspace, 2);

            var reloadedExcluded = Assert.Single(secondContext.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md");
            Assert.True(reloadedExcluded.Node.IsExcluded);
            Assert.Equal(new[] { "chapter-one.md", "chapter-two.md" }, secondContext.Workspace.Nodes.Select(node => node.Node.RelativePath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task IncludeExcludedChapterCommand_IncludesExcludedNodeAndUpdatesBookTxtAndManifestWithoutDuplicateReloadNodes()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-three.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"),
            ("chapter-three.md", "# Chapter Three"));

        try
        {
            using var context = new TestContext(workspace.Root);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            var excludedNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md");
            Assert.True(excludedNode.Node.IsExcluded);

            await ExecuteCommandAsync(context.Workspace.IncludeExcludedChapterCommand.Execute("chapter-two.md"));
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            var reload = await context.Workspace.ReloadCurrentWorkspaceAsync();
            Assert.True(reload.IsSuccess, reload.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            Assert.Equal(new[] { "chapter-one.md", "chapter-three.md", "chapter-two.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Empty(await context.LoadExcludedPathsAsync());
            Assert.Equal(new[] { "chapter-one.md", "chapter-three.md", "chapter-two.md" }, context.Workspace.Nodes.Select(node => node.Node.RelativePath));
            Assert.Equal(1, context.Workspace.Nodes.Count(node => node.Node.RelativePath == "chapter-two.md"));
            Assert.False(context.Workspace.Nodes.Single(node => node.Node.RelativePath == "chapter-two.md").Node.IsExcluded);
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RemoveFromBookCommand_ExcludesIncludedNodeAndUpdatesBookTxtAndManifest()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-two.md\nchapter-three.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"),
            ("chapter-three.md", "# Chapter Three"));

        try
        {
            using var context = new TestContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            await ExecuteCommandAsync(context.Workspace.RemoveFromBookCommand.Execute("chapter-two.md"));
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            Assert.Equal(new[] { "chapter-one.md", "chapter-three.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Equal(new[] { "chapter-two.md" }, await context.LoadExcludedPathsAsync());
            var excludedNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md");
            Assert.True(excludedNode.Node.IsExcluded);
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RemoveMissingEntryCommand_RemovesMissingBookTxtEntryWithoutCreatingManifestExclusion()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-missing.md\nchapter-three.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-three.md", "# Chapter Three"));

        try
        {
            using var context = new TestContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            var missingNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-missing.md");
            Assert.True(missingNode.Node.IsMissing);
            Assert.False(missingNode.Node.IsExcluded);

            await ExecuteCommandAsync(context.Workspace.RemoveMissingEntryCommand.Execute("chapter-missing.md"));
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);

            Assert.Equal(new[] { "chapter-one.md", "chapter-three.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Empty(await context.LoadExcludedPathsAsync());
            Assert.DoesNotContain(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-missing.md");
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task IncludeExcludedChapterCommand_WhenStructureFails_NotifiesAndLeavesExcludedSidebarStateIntact()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new TestContext(workspace.Root, new FailingIncludeStructureService("simulated manifest save after Book.txt write failure"));
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);
            var before = context.Workspace.Nodes.Select(node => (node.Node.RelativePath, node.Node.IsExcluded)).ToArray();

            await ExecuteCommandAsync(context.Workspace.IncludeExcludedChapterCommand.Execute("chapter-two.md"));

            Assert.Equal(before, context.Workspace.Nodes.Select(node => (node.Node.RelativePath, node.Node.IsExcluded)).ToArray());
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("Include file for 'chapter-two.md'", error);
            Assert.Contains("simulated manifest save after Book.txt write failure", error);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RemoveFromBookCommand_WhenStructureFails_NotifiesAndLeavesIncludedSidebarStateIntact()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-two.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new TestContext(workspace.Root, new FailingExcludeStructureService("simulated Book.txt write or validation failure"));
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);
            var before = context.Workspace.Nodes.Select(node => (node.Node.RelativePath, node.Node.IsExcluded)).ToArray();

            await ExecuteCommandAsync(context.Workspace.RemoveFromBookCommand.Execute("chapter-two.md"));

            Assert.Equal(before, context.Workspace.Nodes.Select(node => (node.Node.RelativePath, node.Node.IsExcluded)).ToArray());
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("Failed to remove chapter", error);
            Assert.Contains("simulated Book.txt write or validation failure", error);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task TrySwitchChapterAsync_WhenNodeIsExcluded_DoesNotOpenExcludedFileAndRestoresPreviousSelection()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new TestContext(workspace.Root);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);

            var includedNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-one.md");
            var excludedNode = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md");

            await context.Editor.OpenChapterAsync(includedNode.Node, Path.Combine(workspace.Root, includedNode.Node.RelativePath));
            context.Workspace.SelectedNode = includedNode;

            await InvokeTrySwitchChapterAsync(context.Workspace, excludedNode);

            Assert.Same(includedNode, context.Workspace.SelectedNode);
            Assert.Same(includedNode.Node, context.Editor.ActiveNode);
            Assert.Equal(Path.Combine(workspace.Root, "chapter-one.md"), context.Editor.ActiveFilePath);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    private static (string Root, string BookTxtPath) CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-sidebar-exclusions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
            WriteWorkspaceFile(root, relativePath, content);

        return (root, Path.Combine(root, "Book.txt"));
    }

    private static void WriteWorkspaceFile(string root, string relativePath, string content)
    {
        var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(absolutePath, content);
    }

    private static string[] ReadBookTxtLines(string bookTxtPath) => File.ReadAllLines(bookTxtPath);

    private static void DeleteWorkspace(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static async Task InvokeTrySwitchChapterAsync(WorkspaceViewModel workspace, ChapterViewModel chapter)
    {
        var method = typeof(WorkspaceViewModel).GetMethod("TrySwitchChapterAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TrySwitchChapterAsync was not found.");

        var task = (Task)(method.Invoke(workspace, new object[] { chapter })
            ?? throw new InvalidOperationException("TrySwitchChapterAsync returned null."));
        await task;
    }

    private static async Task ExecuteCommandAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private static async Task WaitForNodesAsync(WorkspaceViewModel workspace, int expectedCount)
    {
        await WaitUntilAsync(() => workspace.Nodes.Count == expectedCount,
            $"Workspace nodes did not reach expected count {expectedCount}; actual count was {workspace.Nodes.Count}.");
    }

    private static async Task WaitForAnyNodesAsync(WorkspaceViewModel workspace, RecordingNotificationService notifications)
    {
        await WaitUntilAsync(
            () => workspace.Nodes.Count > 0 || notifications.Errors.Count > 0,
            $"Workspace nodes did not load; actual count was {workspace.Nodes.Count}; HasWorkspace={workspace.HasWorkspace}; errors={FormatErrors(notifications)}.");

        if (workspace.Nodes.Count == 0)
            Assert.Fail($"Workspace nodes did not load; actual count was {workspace.Nodes.Count}; HasWorkspace={workspace.HasWorkspace}; errors={FormatErrors(notifications)}.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && !predicate())
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        if (!predicate())
            Assert.Fail(failureMessage);

        ReactiveUiTestBootstrap.RunOnUiThread(() => { });
    }

    private static string FormatErrors(RecordingNotificationService notifications) =>
        notifications.Errors.Count == 0 ? "none" : string.Join(" | ", notifications.Errors);

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public RecordingNotificationService NotificationService { get; } = new();
        public MetadataStore MetadataStore { get; } = new();
        public InMemoryAppSettingsStore SettingsStore { get; } = new();
        public PhaseDataService PhaseDataService { get; }
        public TargetsService TargetsService { get; }
        public WordCountService WordCountService { get; } = new();
        public StubFolderPickerService FolderPickerService { get; }
        public StubFilePickerService FilePickerService { get; } = new();
        public ExclusionManifestService ExclusionManifestService { get; }
        public ManuscriptService ManuscriptService { get; }
        public OrphanFileDiscoveryService OrphanFileDiscoveryService { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string workspaceRoot, IBookTxtStructureService? structureService = null)
        {
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = new ExclusionManifestService(MetadataStore);
            var effectiveStructureService = structureService ?? new BookTxtStructureService(MetadataStore, ExclusionManifestService);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            var registry = new ChapterRegistryService(MetadataStore);
            var history = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService, ExclusionManifestService));

            Workspace = new WorkspaceViewModel(
                ManuscriptService,
                effectiveStructureService,
                FilePickerService,
                SettingsStore,
                FolderPickerService,
                NotificationService,
                Editor,
                registry,
                PhaseDataService,
                TargetsService,
                WordCountService,
                history,
                ExclusionManifestService,
                OrphanFileDiscoveryService);
        }

        public void Dispose() => _owned.Dispose();

        public async Task OpenWorkspaceAsync()
        {
            var result = await ManuscriptService.LoadWorkspaceAsync(FolderPickerService.WorkspaceRoot);
            Assert.True(result.IsSuccess, result.Error);

            var model = result.Value!;
            var activeNodes = model.Nodes.Items.OrderBy(node => node.Index).ToList();
            var activePaths = activeNodes.Select(node => node.RelativePath).ToList();
            var projectionMethod = typeof(WorkspaceViewModel).GetMethod("ProjectSidebarNodesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate WorkspaceViewModel sidebar projection method.");
            var projectionTask = (Task<IReadOnlyList<ChapterNode>>)projectionMethod.Invoke(Workspace, new object[] { model, activeNodes, activePaths })!;
            var projectedNodes = await projectionTask;

            SeedWorkspace(model, projectedNodes);
        }

        private void SeedWorkspace(ManuscriptModel model, IReadOnlyList<ChapterNode> nodes)
        {
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(model.WorkspaceRoot));

            var workspaceNodes = GetPrivateList<ChapterViewModel>(Workspace, "_nodes");
            var visibleNodes = GetPrivateList<ChapterViewModel>(Workspace, "_visibleNodes");
            var lookup = GetPrivateDictionary<string, ChapterViewModel>(Workspace, "_nodesByPath");

            foreach (var node in workspaceNodes.OfType<IDisposable>())
                node.Dispose();

            workspaceNodes.Clear();
            visibleNodes.Clear();
            lookup.Clear();

            foreach (var node in nodes.OrderBy(node => node.Index))
            {
                var vm = new ChapterViewModel(
                    node,
                    uuid: string.Empty,
                    phaseData: null,
                    TestMetadataStores.Sidecar(PhaseDataService, TargetsService),
                    SettingsStore,
                    NotificationService,
                    model.WorkspaceRoot);
                workspaceNodes.Add(vm);
                visibleNodes.Add(vm);
                lookup[node.RelativePath] = vm;
            }
        }

        private static void SetPrivateField<T>(WorkspaceViewModel workspace, string fieldName, T value)
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            field.SetValue(workspace, value);
        }

        private static void SetPrivateProperty<T>(WorkspaceViewModel workspace, string propertyName, T value)
        {
            var property = typeof(WorkspaceViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel property '{propertyName}'.");
            property.SetValue(workspace, value);
        }

        private static IList<T> GetPrivateList<T>(WorkspaceViewModel workspace, string fieldName)
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IList<T>)field.GetValue(workspace)!;
        }

        private static IDictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(WorkspaceViewModel workspace, string fieldName)
            where TKey : notnull
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IDictionary<TKey, TValue>)field.GetValue(workspace)!;
        }

        public async Task<string[]> LoadExcludedPathsAsync()
        {
            var result = await ExclusionManifestService.LoadAsync(Workspace.WorkspaceRoot);
            Assert.True(result.IsSuccess, result.Error);
            return result.Value!.ExcludedPaths;
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

        public Task<T?> GetAsync<T>(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is T typed)
                return Task.FromResult<T?>(typed);

            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFolderPickerService : IFolderPickerService
    {
        private readonly string _workspaceRoot;

        public StubFolderPickerService(string workspaceRoot) => _workspaceRoot = workspaceRoot;

        public string WorkspaceRoot => _workspaceRoot;

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(_workspaceRoot);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class FailingIncludeStructureService : DelegatingStructureService
    {
        private readonly string _error;

        public FailingIncludeStructureService(string error) => _error = error;

        public override Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Task.FromResult(Result<Unit>.Fail(_error));
    }

    private sealed class FailingExcludeStructureService : DelegatingStructureService
    {
        private readonly string _error;

        public FailingExcludeStructureService(string error) => _error = error;

        public override Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Fail(_error));
    }

    private abstract class DelegatingStructureService : IBookTxtStructureService
    {
        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public virtual Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public virtual Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));
    }
}
