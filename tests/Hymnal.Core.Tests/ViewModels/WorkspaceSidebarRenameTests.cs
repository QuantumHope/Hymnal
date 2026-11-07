using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using ReactiveUI;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class WorkspaceSidebarRenameTests
{
    static WorkspaceSidebarRenameTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task RenameChapterCommand_RenamesViaCoreReloadsAndPreservesUuidBackedMetadata()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One\n\nBody words."));

        try
        {
            using var context = new TestContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForNodeAsync(context.Workspace, "part-one/chapter-one.md");

            var original = context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-one/chapter-one.md");
            var uuid = original.Uuid;
            Assert.False(string.IsNullOrWhiteSpace(uuid));
            await SeedUuidSidecarsAsync(workspace.Root, uuid);

            var chapterRename = new RenameChapterRequest("part-one/chapter-one.md", "Better Chapter");
            await WaitForCommandCanExecuteAsync(context.Workspace.RenameChapterCommand, chapterRename);
            await InvokePrivateTaskOnUiThreadWithPumpingAsync(context.Workspace, "RenameChapterAsync", chapterRename);
            Assert.True(context.NotificationService.Errors.Count == 0, string.Join(" | ", context.NotificationService.Errors));
            await context.OpenWorkspaceAsync();
            await WaitForNodeAsync(context.Workspace, "part-one/better-chapter.md");

            Assert.Equal(new[] { "part-one/part.md", "part-one/better-chapter.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/better-chapter.md")));
            Assert.Contains("# Better Chapter", await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "part-one/better-chapter.md")));

            var renamed = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "part-one/better-chapter.md");
            Assert.Equal(uuid, renamed.Uuid);
            Assert.Equal(ChapterStatus.Drafting, renamed.Status);
            Assert.Equal(1000, renamed.Target?.MinWords);
            Assert.Equal(1500, renamed.Target?.MaxWords);
            Assert.Equal(2, context.Workspace.Nodes.Count);

            await AssertUuidSidecarsAsync(workspace.Root, uuid);
            var registry = await context.RegistryService.LoadAsync(workspace.Root);
            Assert.Equal("part-one/better-chapter.md", registry[uuid].CurrentPath);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RenamePartCommand_RenamesContainingFolderUpdatesChildrenAndPreservesChildUuids()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-a.md\npart-one/chapter-b.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-a.md", "# Chapter A"),
            ("part-one/chapter-b.md", "# Chapter B"));

        try
        {
            using var context = new TestContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForNodesAsync(context.Workspace, 3);

            var partUuid = context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-one/part.md").Uuid;
            var childAUuid = context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-one/chapter-a.md").Uuid;
            var childBUuid = context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-one/chapter-b.md").Uuid;
            Assert.All(new[] { partUuid, childAUuid, childBUuid }, uuid => Assert.False(string.IsNullOrWhiteSpace(uuid)));

            var partRename = new RenamePartRequest("part-one/part.md", "Part Two");
            await WaitForCommandCanExecuteAsync(context.Workspace.RenamePartCommand, partRename);
            await InvokePrivateTaskOnUiThreadWithPumpingAsync(context.Workspace, "RenamePartAsync", partRename);
            Assert.True(context.NotificationService.Errors.Count == 0, string.Join(" | ", context.NotificationService.Errors));
            await context.OpenWorkspaceAsync();
            await WaitForNodeAsync(context.Workspace, "part-two/part.md");

            Assert.Equal(new[] { "part-two/part.md", "part-two/chapter-a.md", "part-two/chapter-b.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "part-one")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/part.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-a.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-b.md")));
            Assert.Contains("# Part Two", await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "part-two/part.md")));

            Assert.Equal(partUuid, context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-two/part.md").Uuid);
            Assert.Equal(childAUuid, context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-two/chapter-a.md").Uuid);
            Assert.Equal(childBUuid, context.Workspace.Nodes.Single(node => node.Node.RelativePath == "part-two/chapter-b.md").Uuid);
            Assert.Equal(3, context.Workspace.Nodes.Select(node => node.Node.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(context.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RenameChapterCommand_WhenCoreConflictFails_NotifiesAndLeavesSidebarBookTxtAndFilesUnchanged()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Existing Target"));

        try
        {
            using var context = new TestContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForNodeAsync(context.Workspace, "chapter-one.md");

            var beforeNodes = context.Workspace.Nodes.Select(node => node.Node.RelativePath).ToArray();

            var conflictRename = new RenameChapterRequest("chapter-one.md", "Chapter Two");
            await WaitForCommandCanExecuteAsync(context.Workspace.RenameChapterCommand, conflictRename);
            await InvokePrivateTaskOnUiThreadWithPumpingAsync(context.Workspace, "RenameChapterAsync", conflictRename);

            Assert.Equal(new[] { "chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "chapter-two.md")));
            Assert.Equal(beforeNodes, context.Workspace.Nodes.Select(node => node.Node.RelativePath).ToArray());
            Assert.DoesNotContain(context.Workspace.Nodes, node => node.Node.RelativePath == "chapter-two.md");

            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("Rename chapter", error);
            Assert.Contains("chapter-one.md", error);
            Assert.Contains("chapter-two.md", error);
            Assert.Contains("target path", error);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task RenameChapterCommand_RejectsBlankTitleBeforeCallingCore()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure);
            await context.OpenWorkspaceAsync();
            await WaitForNodeAsync(context.Workspace, "chapter-one.md");

            var blankRename = new RenameChapterRequest("chapter-one.md", "   ");
            await WaitForCommandCanExecuteAsync(context.Workspace.RenameChapterCommand, blankRename);
            await InvokePrivateTaskOnUiThreadWithPumpingAsync(context.Workspace, "RenameChapterAsync", blankRename);

            Assert.Equal(0, structure.RenameCalls);
            Assert.Equal(new[] { "chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("chapter title is required", error);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    private static (string Root, string BookTxtPath) CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-sidebar-renames-{Guid.NewGuid():N}");
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

    private static async Task SeedUuidSidecarsAsync(string workspaceRoot, string uuid)
    {
        var store = new MetadataStore();
        await new PhaseDataService(store).SaveAsync(workspaceRoot, new Dictionary<string, PhaseData>
        {
            [uuid] = new() { Status = ChapterStatus.Drafting }
        });
        await new TargetsService(store).SaveAsync(workspaceRoot, new Dictionary<string, WordCountTarget>
        {
            [uuid] = new() { MinWords = 1000, MaxWords = 1500 }
        });
        await new WordCountHistoryService(store).AppendAsync(workspaceRoot, uuid, "2026-06-04", 1234);

        var notesDirectory = Path.Combine(workspaceRoot, ".hymnal-data", "notes");
        Directory.CreateDirectory(notesDirectory);
        await File.WriteAllTextAsync(Path.Combine(notesDirectory, uuid + ".md"), "UUID keyed note");
    }

    private static async Task AssertUuidSidecarsAsync(string workspaceRoot, string uuid)
    {
        var store = new MetadataStore();
        var phases = await new PhaseDataService(store).LoadAsync(workspaceRoot);
        Assert.Equal(ChapterStatus.Drafting, phases[uuid].Status);

        var targets = await new TargetsService(store).LoadAsync(workspaceRoot);
        Assert.Equal(1000, targets[uuid].MinWords);
        Assert.Equal(1500, targets[uuid].MaxWords);

        var history = await new WordCountHistoryService(store).GetAllAsync(workspaceRoot);
        var historyEntry = Assert.Single(history, entry => entry.Uuid == uuid);
        Assert.Equal("2026-06-04", historyEntry.Date);
        Assert.Equal(1234, historyEntry.WordCount);

        Assert.Equal("UUID keyed note", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, ".hymnal-data", "notes", uuid + ".md")));
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

    private static async Task ExecuteCommandWithPumpingAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!completion.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        if (!completion.Task.IsCompleted)
            Assert.Fail("Command execution did not complete before timeout.");

        await completion.Task;
        ReactiveUiTestBootstrap.RunOnUiThread(() => { });
    }

    private static async Task WaitForCommandCanExecuteAsync(ICommand command, object parameter)
    {
        await WaitUntilAsync(
            () => command.CanExecute(parameter),
            $"Command did not become executable for parameter '{parameter}'.");
    }

    private static async Task InvokePrivateTaskOnUiThreadWithPumpingAsync<TRequest>(WorkspaceViewModel workspace, string methodName, TRequest request)
    {
        var method = typeof(WorkspaceViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel method '{methodName}'.");

        var task = ReactiveUiTestBootstrap.RunOnUiThread(() => (Task)(method.Invoke(workspace, new object?[] { request })
            ?? throw new InvalidOperationException($"WorkspaceViewModel method '{methodName}' returned null.")));

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
        await WaitUntilAsync(() => workspace.Nodes.Count == expectedCount && workspace.Nodes.All(node => !string.IsNullOrWhiteSpace(node.Uuid)),
            $"Workspace nodes did not reach expected count {expectedCount} with UUIDs; actual count was {workspace.Nodes.Count}.");
    }

    private static async Task WaitForNodeAsync(WorkspaceViewModel workspace, string relativePath)
    {
        await WaitUntilAsync(
            () => workspace.Nodes.Any(node => node.Node.RelativePath == relativePath && !string.IsNullOrWhiteSpace(node.Uuid)),
            $"Workspace node '{relativePath}' did not load with a UUID; nodes were: {string.Join(", ", workspace.Nodes.Select(node => node.Node.RelativePath + ":" + node.Uuid))}; Book.txt={SafeReadBookTxt(workspace.BookTxtPath)}; targetFileExists={File.Exists(Path.Combine(workspace.ManuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))}");
    }

    private static string SafeReadBookTxt(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? "<missing>" : File.ReadAllText(path).Replace("\r", "\\r").Replace("\n", "\\n"); }
        catch (Exception ex) { return $"<error: {ex.Message}>"; }
    }

    private static async Task WaitForSelectedNodeAsync(WorkspaceViewModel workspace, string relativePath)
    {
        await WaitUntilAsync(
            () => workspace.SelectedNode?.Node.RelativePath == relativePath,
            $"Workspace selected node did not become '{relativePath}'; actual was '{workspace.SelectedNode?.Node.RelativePath ?? "<null>"}'.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !predicate())
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        if (!predicate())
            Assert.Fail(failureMessage);

        ReactiveUiTestBootstrap.RunOnUiThread(() => { });
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

        public TestContext(string workspaceRoot, IBookTxtStructureService? structureService = null)
        {
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = new ExclusionManifestService(MetadataStore);
            RegistryService = new ChapterRegistryService(MetadataStore);
            var effectiveStructureService = structureService ?? new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService));
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
        public int RenameCalls { get; private set; }

        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
        {
            RenameCalls++;
            return Task.FromResult(Result<Unit>.Ok(Unit.Default));
        }

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));
    }
}
