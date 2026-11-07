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
public sealed class WorkspaceSidebarReorderTests
{
    static WorkspaceSidebarReorderTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task ReorderChapterCommand_ReordersIncludedChapterWithinSamePartUsingActiveBookTxtIndexAndReloadsWithoutDuplicates()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-one/chapter-three.md", "# Chapter Three"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "part-one/chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 4);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("part-one/chapter-one.md", AfterRelativePath: "part-one/chapter-three.md"));
            await WaitForNodesAsync(context.Workspace, 4);

            var call = Assert.Single(structure.ReorderCalls);
            Assert.Equal("part-one/chapter-one.md", call.Path);
            Assert.Equal(2, call.NewIndex);
            Assert.True(call.WatcherWasSuppressed);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-three.md", "part-one/chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));

            await context.OpenWorkspaceAsync();
            await WaitForVisibleOrderAsync(context.Workspace,
                "part-one/part.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md");
            Assert.Equal(
                new[] { "part-one/part.md", "part-one/chapter-three.md", "part-one/chapter-one.md", "part-one/chapter-two.md" },
                context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath));
            Assert.Equal(context.Workspace.VisibleNodes.Count, context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_ReordersIncludedPartAsWholeBlockWhenDroppedOnPartDivider()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md\npart-three/part.md\npart-three/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"),
            ("part-three/part.md", "{class: part}\n# Part Three"),
            ("part-three/chapter-three.md", "# Chapter Three"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 6);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("part-three/part.md", BeforeRelativePath: "part-two/part.md"));
            await WaitForNodesAsync(context.Workspace, 6);

            var call = Assert.Single(structure.ReorderCalls);
            Assert.Equal("part-three/part.md", call.Path);
            Assert.Equal(2, call.NewIndex);
            Assert.True(call.WatcherWasSuppressed);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-three/part.md",
                "part-three/chapter-three.md",
                "part-two/part.md",
                "part-two/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));

            await context.OpenWorkspaceAsync();
            await WaitForVisibleOrderAsync(context.Workspace,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-three/part.md",
                "part-three/chapter-three.md",
                "part-two/part.md",
                "part-two/chapter-two.md");
            Assert.Equal(ReadBookTxtLines(workspace.BookTxtPath), context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath));
            Assert.Equal(6, context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_CoreFailureShowsErrorAndLeavesVisibleOrderUnchanged()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var structure = new RecordingStructureService
            {
                ReorderFailure = "Book.txt validation rejected simulated reorder."
            };
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForVisibleOrderAsync(context.Workspace,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md");

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("part-one/chapter-one.md", AfterRelativePath: "part-one/chapter-two.md"));

            var call = Assert.Single(structure.ReorderCalls);
            Assert.Equal("part-one/chapter-one.md", call.Path);
            Assert.Equal(2, call.NewIndex);
            Assert.True(call.WatcherWasSuppressed);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("Failed to reorder sidebar entry 'part-one/chapter-one.md'", error);
            Assert.Contains("Book.txt validation rejected simulated reorder", error);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-one.md", "part-one/chapter-two.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Equal(
                new[] { "part-one/part.md", "part-one/chapter-one.md", "part-one/chapter-two.md" },
                context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath));
            Assert.Equal(context.Workspace.VisibleNodes.Count, context.Workspace.VisibleNodes.Select(node => node.Node.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_RejectsExcludedSourceBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("chapter-two.md", BeforeRelativePath: "chapter-one.md"));

            Assert.Empty(structure.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("chapter-two.md", error);
            Assert.Contains("excluded", error);
            Assert.Equal(new[] { "chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_RejectsMissingSourceBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-missing.md"),
            ("chapter-one.md", "# Chapter One"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("chapter-missing.md", BeforeRelativePath: "chapter-one.md"));

            Assert.Empty(structure.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("chapter-missing.md", error);
            Assert.Contains("missing", error);
            Assert.Equal(new[] { "chapter-one.md", "chapter-missing.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_RejectsPartDropOntoChapterBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 4);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("part-two/part.md", BeforeRelativePath: "part-one/chapter-one.md"));

            Assert.Empty(structure.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("part-two/part.md", error);
            Assert.Contains("only be dropped on another Part", error);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-one.md", "part-two/part.md", "part-two/chapter-two.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_RejectsCrossPartChapterMovementWithUserVisibleNotificationBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 4);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("part-one/chapter-one.md", AfterRelativePath: "part-two/chapter-two.md"));

            Assert.Empty(structure.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("part-one/chapter-one.md", error);
            Assert.Contains("cannot be moved across Part sections", error);
            Assert.Contains("part-one/part.md", error);
            Assert.Contains("part-two/part.md", error);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-one.md", "part-two/part.md", "part-two/chapter-two.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ReorderChapterCommand_RejectsMissingTargetBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md\nchapter-two.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 2);

            await InvokePrivateTaskOnUiThreadWithPumpingAsync(
                context.Workspace,
                "ReorderChapterAsync",
                new ReorderCardRequest("chapter-one.md", BeforeRelativePath: "not-in-book.md"));

            Assert.Empty(structure.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("not-in-book.md", error);
            Assert.Contains("not found", error);
            Assert.Equal(new[] { "chapter-one.md", "chapter-two.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    private static (string Root, string BookTxtPath) CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-sidebar-reorders-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
            WriteWorkspaceFile(root, relativePath, content);

        return (root, Path.Combine(root, "Book.txt"));
    }

    private static void WriteWorkspaceFile(string root, string relativePath, string content)
    {
        var absolutePath = AbsolutePath(root, relativePath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(absolutePath, content);
    }

    private static string AbsolutePath(string workspaceRoot, string relativePath) =>
        Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string[] ReadBookTxtLines(string bookTxtPath) => File.ReadAllLines(bookTxtPath);

    private static void DeleteWorkspace(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static async Task InvokePrivateTaskOnUiThreadWithPumpingAsync<TRequest>(WorkspaceViewModel workspace, string methodName, TRequest request)
    {
        var method = typeof(WorkspaceViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel method '{methodName}'.");

        var task = (Task)(method.Invoke(workspace, new object?[] { request })
            ?? throw new InvalidOperationException($"WorkspaceViewModel method '{methodName}' returned null."));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        if (!task.IsCompleted)
            Assert.Fail($"WorkspaceViewModel method '{methodName}' did not complete before timeout.");

        await task;
        ReactiveUiTestBootstrap.RunOnUiThread(() => { });
    }

    private static async Task WaitForNodesAsync(WorkspaceViewModel workspace, int expectedCount)
    {
        await WaitUntilAsync(
            () => workspace.Nodes.Count == expectedCount,
            $"Workspace nodes did not reach expected count {expectedCount}; actual count was {workspace.Nodes.Count}. Nodes: {string.Join(", ", workspace.Nodes.Select(node => node.Node.RelativePath))}");
    }

    private static async Task WaitForVisibleOrderAsync(WorkspaceViewModel workspace, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => workspace.VisibleNodes.Select(node => node.Node.RelativePath).SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase),
            $"Workspace visible nodes did not reach expected order {string.Join(", ", expectedPaths)}; actual order was {string.Join(", ", workspace.VisibleNodes.Select(node => node.Node.RelativePath))}.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;

            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        Assert.Fail(failureMessage);
    }

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
        public ChapterRegistryService RegistryService { get; }
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string workspaceRoot, RecordingStructureService? structureService = null)
        {
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = new ExclusionManifestService(MetadataStore);
            RegistryService = new ChapterRegistryService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService));
            var history = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService, ExclusionManifestService));
            var effectiveStructureService = structureService ?? new RecordingStructureService();
            effectiveStructureService.Configure(new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService), ManuscriptService);

            Workspace = new WorkspaceViewModel(
                ManuscriptService,
                effectiveStructureService,
                FilePickerService,
                SettingsStore,
                FolderPickerService,
                NotificationService,
                Editor,
                RegistryService,
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

            var registry = await RegistryService.LoadAsync(model.WorkspaceRoot);
            registry = RegistryService.ReconcileOrphans(registry, activePaths);
            foreach (var node in activeNodes)
                RegistryService.AssignUuid(registry, node.RelativePath, node.Title);
            await RegistryService.SaveAsync(model.WorkspaceRoot, registry);

            var phases = await PhaseDataService.LoadAsync(model.WorkspaceRoot);
            var targets = await TargetsService.LoadAsync(model.WorkspaceRoot);
            SeedWorkspace(model, projectedNodes, registry, phases, targets);
        }

        private void SeedWorkspace(
            ManuscriptModel model,
            IReadOnlyList<ChapterNode> nodes,
            IReadOnlyDictionary<string, ChapterRegistryEntry> registry,
            IReadOnlyDictionary<string, PhaseData> phases,
            Dictionary<string, WordCountTarget> targets)
        {
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(model.WorkspaceRoot));

            var workspaceNodes = GetPrivateList<ChapterViewModel>(Workspace, "_nodes");
            var visibleNodes = GetPrivateList<ChapterViewModel>(Workspace, "_visibleNodes");
            var lookup = GetPrivateDictionary<string, ChapterViewModel>(Workspace, "_nodesByPath");

            foreach (var node in workspaceNodes.Where(node => node is IDisposable).Cast<IDisposable>())
                node.Dispose();

            workspaceNodes.Clear();
            visibleNodes.Clear();
            lookup.Clear();

            foreach (var node in nodes.OrderBy(node => node.Index))
            {
                var uuid = registry.Values.FirstOrDefault(entry => string.Equals(entry.CurrentPath, node.RelativePath, StringComparison.OrdinalIgnoreCase))?.Uuid ?? string.Empty;
                phases.TryGetValue(uuid, out var phaseData);
                var target = string.IsNullOrWhiteSpace(uuid) ? null : TargetsService.GetTarget(targets, uuid);
                var vm = new ChapterViewModel(
                    node,
                    uuid,
                    phaseData,
                    PhaseDataService,
                    TargetsService,
                    SettingsStore,
                    NotificationService,
                    model.WorkspaceRoot,
                    target);
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

    private sealed class RecordingStructureService : IBookTxtStructureService
    {
        private IBookTxtStructureService? _inner;
        private ManuscriptService? _manuscriptService;

        public List<ReorderCall> ReorderCalls { get; } = new();

        public string? ReorderFailure { get; init; }

        public void Configure(IBookTxtStructureService inner, ManuscriptService manuscriptService)
        {
            _inner = inner;
            _manuscriptService = manuscriptService;
        }

        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) =>
            Inner.ReadNormalizedEntriesAsync(bookTxtPath);

        public async Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
        {
            ReorderCalls.Add(new ReorderCall(bookTxtPath, chapterPath, newIndex, ReadSuppressCount() > 0));
            if (!string.IsNullOrWhiteSpace(ReorderFailure))
                return Result<Unit>.Fail(ReorderFailure);

            return await Inner.ReorderEntryAsync(bookTxtPath, chapterPath, newIndex);
        }

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath) =>
            Inner.RenameEntryAsync(bookTxtPath, existingPath, replacementPath);

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Inner.AddExistingEntryAsync(bookTxtPath, chapterPath, index);

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Inner.AddExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);

        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Inner.IncludeExistingEntryAsync(bookTxtPath, chapterPath, index);

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Inner.IncludeExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) =>
            Inner.CreateNewChapterAsync(bookTxtPath, chapterPath, content, index);

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) =>
            Inner.CreateNewPartAsync(bookTxtPath, partPath, title, index);

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) =>
            Inner.RemoveEntryAsync(bookTxtPath, chapterPath);

        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) =>
            Inner.ExcludeEntryAsync(bookTxtPath, chapterPath);

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) =>
            Inner.DeleteChapterFileAsync(bookTxtPath, chapterPath);

        private IBookTxtStructureService Inner => _inner ?? throw new InvalidOperationException("Recording structure service was not configured.");

        private int ReadSuppressCount()
        {
            if (_manuscriptService == null)
                return 0;

            var field = typeof(ManuscriptService).GetField("_suppressCount", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate ManuscriptService watcher suppression field.");
            return (int)field.GetValue(_manuscriptService)!;
        }
    }

    private sealed record ReorderCall(string BookTxtPath, string Path, int NewIndex, bool WatcherWasSuppressed);
}
