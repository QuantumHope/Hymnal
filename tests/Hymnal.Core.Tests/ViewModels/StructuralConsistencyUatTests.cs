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
public sealed class StructuralConsistencyUatTests
{
    static StructuralConsistencyUatTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task CrossSurfaceReplay_PersistsOneUuidBackedStructureAfterFreshReload()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", string.Join('\n',
                "front/part.md",
                "front/alpha.md",
                "front/beta.md",
                "middle/part.md",
                "middle/act-one/part.md",
                "middle/act-one/gamma.md",
                "middle/act-one/delta.md",
                "back/part.md",
                "back/omega.md")),
            ("front/part.md", "{class: part}\n# Front Matter"),
            ("front/alpha.md", "# Alpha\n\nOriginal alpha body."),
            ("front/beta.md", "# Beta\n\nOriginal beta body."),
            ("middle/part.md", "{class: part}\n# Middle"),
            ("middle/act-one/part.md", "{class: part}\n# Act One"),
            ("middle/act-one/gamma.md", "# Gamma\n\nOriginal gamma body."),
            ("middle/act-one/delta.md", "# Delta\n\nOriginal delta body."),
            ("middle/act-one/orphan.md", "# Orphan\n\nExcluded orphan body."),
            ("back/part.md", "{class: part}\n# Back Matter"),
            ("back/omega.md", "# Omega\n\nOriginal omega body."));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure, ownsWorkspace: false);

            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspace.Root, "middle/act-one/orphan.md");
            Assert.True(excluded.IsSuccess, excluded.Error);
            await SeedRegistryAndSidecarsAsync(context, new[]
            {
                "front/alpha.md",
                "front/beta.md",
                "middle/act-one/gamma.md",
                "middle/act-one/delta.md",
                "middle/act-one/orphan.md",
                "back/omega.md"
            });

            await context.OpenWorkspaceAsync(expectedNodeCount: 10);
            var alphaUuid = (await context.WaitForNodeAsync("front/alpha.md", requireUuid: true)).Uuid;
            var betaUuid = (await context.WaitForNodeAsync("front/beta.md", requireUuid: true)).Uuid;
            var gammaUuid = (await context.WaitForNodeAsync("middle/act-one/gamma.md", requireUuid: true)).Uuid;
            var deltaUuid = (await context.WaitForNodeAsync("middle/act-one/delta.md", requireUuid: true)).Uuid;
            var orphanUuid = (await context.WaitForNodeAsync("middle/act-one/orphan.md", requireUuid: true)).Uuid;

            // Sidebar surface: exclude/include, rename, chapter reorder, and Part-block reorder.
            await ExecuteCommandAsync(context.Workspace.RemoveFromBookCommand.Execute("front/beta.md"));
            await context.OpenWorkspaceAsync(expectedNodeCount: 10);
            await WaitUntilAsync(
                () => context.Workspace.Nodes.Any(node => node.Node.RelativePath == "front/beta.md" && node.Node.IsExcluded),
                "Sidebar remove did not project front/beta.md as excluded.");
            await ExecuteCommandAsync(context.Workspace.IncludeExcludedChapterCommand.Execute("front/beta.md"));
            await context.OpenWorkspaceAsync(expectedNodeCount: 10);
            await ExecuteCommandAsync(context.Workspace.RenameChapterCommand.Execute(new RenameChapterRequest("front/alpha.md", "Renamed Alpha")));
            await ExecuteCommandAsync(context.Workspace.ReorderChapterCommand.Execute(new ReorderCardRequest("front/beta.md", AfterRelativePath: "front/renamed-alpha.md")));
            await ExecuteCommandAsync(context.Workspace.ReorderChapterCommand.Execute(new ReorderCardRequest("back/part.md", BeforeRelativePath: "middle/part.md")));

            await WaitForVisibleOrderAsync(context.Workspace,
                "front/part.md",
                "front/renamed-alpha.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md",
                "middle/part.md",
                "middle/act-one/part.md",
                "middle/act-one/gamma.md",
                "middle/act-one/delta.md",
                "middle/act-one/orphan.md");

            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "front/part.md",
                "front/renamed-alpha.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md",
                "middle/part.md",
                "middle/act-one/part.md",
                "middle/act-one/gamma.md",
                "middle/act-one/delta.md");

            // Corkboard surface: same-Part reorder, cross-Part move, include/exclude, and inline create.
            await ExecuteCommandAsync(board.DropCardCommand.Execute(new CorkboardDropRequest("front/renamed-alpha.md", AfterRelativePath: "front/beta.md")));
            await ExecuteCommandAsync(board.DropCardCommand.Execute(new CorkboardDropRequest(
                "front/renamed-alpha.md",
                TargetPartPath: "back/part.md",
                AfterRelativePath: "back/omega.md")));
            await ExecuteCommandAsync(board.RemoveFromBookCommand.Execute(new RemoveChapterRequest("middle/act-one/delta.md")));
            await WaitForExcludedCardAsync(board, "middle/act-one/delta.md");
            await WaitForExcludedCardAsync(board, "middle/act-one/orphan.md");
            await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(new IncludeExistingChapterRequest(
                "middle/act-one/orphan.md",
                board.GetInsertIndexAfterChapter("middle/act-one/gamma.md"))));
            var actOne = GetPartDivider(board, "middle/act-one/part.md");
            board.BeginInlineCreate(board.GetInsertIndexAfterChapter("middle/act-one/orphan.md"), actOne);
            await board.CommitInlineCreateAsync("Inserted Scene");
            Assert.Null(board.LastStructuralError);

            // Gantt surface: same-Part row reorder through the Workspace structural command path.
            var gantt = context.CreateGanttViewModel();
            await WaitForGanttRowsAsync(gantt,
                "__book__",
                "front/part.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md",
                "back/renamed-alpha.md",
                "middle/part.md",
                "middle/act-one/part.md",
                "middle/act-one/gamma.md",
                "middle/act-one/orphan.md",
                "middle/act-one/inserted-scene.md",
                "middle/act-one/delta.md");
            await gantt.MoveRowAfterAsync(
                GetGanttRow(gantt, "middle/act-one/gamma.md"),
                GetGanttRow(gantt, "middle/act-one/inserted-scene.md"));

            var expectedBook = new[]
            {
                "front/part.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md",
                "back/renamed-alpha.md",
                "middle/part.md",
                "middle/act-one/part.md",
                "middle/act-one/orphan.md",
                "middle/act-one/inserted-scene.md",
                "middle/act-one/gamma.md"
            };
            Assert.Equal(expectedBook, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Contains(structure.ReorderCalls, call => call.WatcherWasSuppressed);

            using var reloaded = new TestContext(workspace.Root, new RecordingStructureService(), ownsWorkspace: false);
            await reloaded.OpenWorkspaceAsync(expectedNodeCount: 11);
            using var reloadedBoard = reloaded.CreateBoard();
            var reloadedGantt = reloaded.CreateGanttViewModel();

            await WaitForVisibleOrderAsync(reloaded.Workspace, expectedBook.Concat(new[] { "middle/act-one/delta.md" }).ToArray());
            await WaitForBoardProjectionAsync(reloadedBoard, expectedBook);
            await WaitForGanttRowsAsync(reloadedGantt, new[] { "__book__" }.Concat(expectedBook).Concat(new[] { "middle/act-one/delta.md" }).ToArray());

            AssertNoDuplicatePaths(reloaded.Workspace.Nodes.Select(node => node.Node.RelativePath), "workspace nodes");
            AssertNoDuplicatePaths(
                reloadedBoard.Items
                    .Where(item => item.Kind is CorkboardItemKind.PartDivider or CorkboardItemKind.ChapterCard or CorkboardItemKind.ExcludedChapterCard)
                    .Select(item => item.RelativePath),
                "corkboard items");
            AssertNoDuplicatePaths(reloadedGantt.Rows.Select(row => row.RelativePath), "gantt rows");

            Assert.True(File.Exists(AbsolutePath(workspace.Root, "back/renamed-alpha.md")));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "front/alpha.md")));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "front/renamed-alpha.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "middle/act-one/delta.md")));
            Assert.Equal("# Inserted Scene\n\n", await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "middle/act-one/inserted-scene.md")));

            Assert.Equal(new[] { "middle/act-one/delta.md" }, await reloaded.LoadExcludedPathsAsync());
            Assert.Null(reloadedBoard.LastStructuralError);
            Assert.Empty(context.NotificationService.Errors);
            Assert.Empty(reloaded.NotificationService.Errors);

            var registry = await reloaded.RegistryService.LoadAsync(workspace.Root);
            AssertRegistryPath(registry, alphaUuid, "back/renamed-alpha.md");
            AssertRegistryPath(registry, betaUuid, "front/beta.md");
            AssertRegistryPath(registry, gammaUuid, "middle/act-one/gamma.md");
            AssertRegistryPath(registry, deltaUuid, "middle/act-one/delta.md");
            AssertRegistryPath(registry, orphanUuid, "middle/act-one/orphan.md");
            var insertedUuid = Assert.Single(registry.Values, entry => entry.CurrentPath == "middle/act-one/inserted-scene.md").Uuid;
            Assert.False(string.IsNullOrWhiteSpace(insertedUuid));

            await AssertUuidSidecarsAsync(workspace.Root, alphaUuid, "front/alpha.md");
            await AssertUuidSidecarsAsync(workspace.Root, betaUuid, "front/beta.md");
            await AssertUuidSidecarsAsync(workspace.Root, gammaUuid, "middle/act-one/gamma.md");
            await AssertUuidSidecarsAsync(workspace.Root, deltaUuid, "middle/act-one/delta.md");
            await AssertUuidSidecarsAsync(workspace.Root, orphanUuid, "middle/act-one/orphan.md");
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task ControlledStructuralFailures_AreVisibleAndLeaveStateRecoverableAcrossSurfaces()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", string.Join('\n',
                "front/part.md",
                "front/alpha.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md")),
            ("front/part.md", "{class: part}\n# Front"),
            ("front/alpha.md", "# Alpha\n\nOriginal alpha body."),
            ("front/beta.md", "# Beta\n\nOriginal beta body."),
            ("back/part.md", "{class: part}\n# Back"),
            ("back/omega.md", "# Omega\n\nOriginal omega body."),
            ("back/beta.md", "# Existing Back Beta\n\nThis pre-existing file forces a deterministic Corkboard move conflict."));

        try
        {
            var structure = new RecordingStructureService();
            using var context = new TestContext(workspace.Root, structure, ownsWorkspace: false);
            await SeedRegistryAndSidecarsAsync(context, new[]
            {
                "front/alpha.md",
                "front/beta.md",
                "back/omega.md"
            });

            await context.OpenWorkspaceAsync(expectedNodeCount: 5);
            var alphaUuid = (await context.WaitForNodeAsync("front/alpha.md", requireUuid: true)).Uuid;
            var betaUuid = (await context.WaitForNodeAsync("front/beta.md", requireUuid: true)).Uuid;
            var omegaUuid = (await context.WaitForNodeAsync("back/omega.md", requireUuid: true)).Uuid;

            // Successful setup edits prove the later failures happen after real cross-surface structural activity.
            await ExecuteCommandAsync(context.Workspace.ReorderChapterCommand.Execute(new ReorderCardRequest(
                "front/beta.md",
                BeforeRelativePath: "front/alpha.md")));
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "front/part.md",
                "front/beta.md",
                "front/alpha.md",
                "back/part.md",
                "back/omega.md");
            await ExecuteCommandAsync(board.DropCardCommand.Execute(new CorkboardDropRequest(
                "front/alpha.md",
                BeforeRelativePath: "front/beta.md")));

            var stableBook = new[]
            {
                "front/part.md",
                "front/alpha.md",
                "front/beta.md",
                "back/part.md",
                "back/omega.md"
            };
            await WaitForVisibleOrderAsync(context.Workspace, stableBook);
            await WaitForBoardProjectionAsync(board, stableBook);
            Assert.Equal(stableBook, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Empty(context.NotificationService.Errors);
            Assert.Null(board.LastStructuralError);

            // Corkboard surface: a cross-Part move would target back/beta.md, which already exists on disk.
            await ExecuteCommandAsync(board.DropCardCommand.Execute(new CorkboardDropRequest(
                "front/beta.md",
                TargetPartPath: "back/part.md",
                AfterRelativePath: "back/omega.md")));

            Assert.NotNull(board.LastStructuralError);
            var corkboardError = board.LastStructuralError!;
            Assert.Equal("Drop card", corkboardError.Operation);
            Assert.Equal("front/beta.md", corkboardError.Path);
            Assert.Equal(workspace.BookTxtPath, corkboardError.BookTxtPath);
            Assert.Contains("Move operation failed", corkboardError.Message);
            Assert.Contains("front/beta.md", corkboardError.Message);
            Assert.Contains("back/beta.md", corkboardError.Message);
            Assert.Contains("target file", corkboardError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(context.NotificationService.Errors, message =>
                message.Contains("Drop card", StringComparison.OrdinalIgnoreCase)
                && message.Contains("front/beta.md", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Book.txt", StringComparison.OrdinalIgnoreCase)
                && message.Contains("target file", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(stableBook, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "front/beta.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "back/beta.md")));
            Assert.Equal("# Beta\n\nOriginal beta body.", await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "front/beta.md")));
            Assert.Equal("# Existing Back Beta\n\nThis pre-existing file forces a deterministic Corkboard move conflict.", await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "back/beta.md")));
            await WaitForBoardProjectionAsync(board, stableBook);

            // Gantt surface: row drag delegates to the sidebar/Workspace command, which rejects cross-Part chapter reorders.
            var gantt = context.CreateGanttViewModel();
            await WaitForGanttRowsAsync(gantt, new[] { "__book__" }.Concat(stableBook).ToArray());
            await gantt.MoveRowAfterAsync(
                GetGanttRow(gantt, "front/alpha.md"),
                GetGanttRow(gantt, "back/omega.md"));

            Assert.Contains(context.NotificationService.Errors, message =>
                message.Contains("Reorder failed", StringComparison.OrdinalIgnoreCase)
                && message.Contains("front/alpha.md", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Book.txt", StringComparison.OrdinalIgnoreCase)
                && message.Contains("cannot be moved across Part sections", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Move chapters between Parts from the corkboard", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(stableBook, ReadBookTxtLines(workspace.BookTxtPath));
            await WaitForVisibleOrderAsync(context.Workspace, stableBook);
            await WaitForBoardProjectionAsync(board, stableBook);
            await WaitForGanttRowsAsync(gantt, new[] { "__book__" }.Concat(stableBook).ToArray());
            Assert.Empty(await context.LoadExcludedPathsAsync());

            using var reloaded = new TestContext(workspace.Root, new RecordingStructureService(), ownsWorkspace: false);
            await reloaded.OpenWorkspaceAsync(expectedNodeCount: 5);
            using var reloadedBoard = reloaded.CreateBoard();
            var reloadedGantt = reloaded.CreateGanttViewModel();
            await WaitForVisibleOrderAsync(reloaded.Workspace, stableBook);
            await WaitForBoardProjectionAsync(reloadedBoard, stableBook);
            await WaitForGanttRowsAsync(reloadedGantt, new[] { "__book__" }.Concat(stableBook).ToArray());

            var registry = await reloaded.RegistryService.LoadAsync(workspace.Root);
            AssertRegistryPath(registry, alphaUuid, "front/alpha.md");
            AssertRegistryPath(registry, betaUuid, "front/beta.md");
            AssertRegistryPath(registry, omegaUuid, "back/omega.md");
            await AssertUuidSidecarsAsync(workspace.Root, alphaUuid, "front/alpha.md");
            await AssertUuidSidecarsAsync(workspace.Root, betaUuid, "front/beta.md");
            await AssertUuidSidecarsAsync(workspace.Root, omegaUuid, "back/omega.md");
            Assert.Null(reloadedBoard.LastStructuralError);
            Assert.Empty(reloaded.NotificationService.Errors);
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }


    private static (string Root, string BookTxtPath) CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-structural-uat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
            WriteWorkspaceFile(root, relativePath, content);
        return (root, Path.Combine(root, "Book.txt"));
    }

    private static void WriteWorkspaceFile(string root, string relativePath, string content)
    {
        var absolutePath = AbsolutePath(root, relativePath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(absolutePath, content);
    }

    private static string AbsolutePath(string workspaceRoot, string relativePath) =>
        Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string[] ReadBookTxtLines(string bookTxtPath) => File.ReadAllLines(bookTxtPath);

    private static void DeleteWorkspace(string workspaceRoot)
    {
        try
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static async Task SeedRegistryAndSidecarsAsync(TestContext context, IEnumerable<string> chapterPaths)
    {
        var registry = await context.RegistryService.LoadAsync(context.WorkspaceRoot);
        foreach (var path in chapterPaths)
        {
            var title = Path.GetFileNameWithoutExtension(path).Replace('-', ' ');
            context.RegistryService.AssignUuid(registry, path, title);
        }
        await context.RegistryService.SaveAsync(context.WorkspaceRoot, registry);

        registry = await context.RegistryService.LoadAsync(context.WorkspaceRoot);
        foreach (var path in chapterPaths)
        {
            var uuid = registry.Values.Single(entry => entry.CurrentPath == path).Uuid;
            await SeedUuidSidecarsAsync(context.WorkspaceRoot, uuid, path);
        }
    }

    private static async Task SeedUuidSidecarsAsync(string workspaceRoot, string uuid, string originalPath)
    {
        var store = new MetadataStore();
        var phases = await new PhaseDataService(store).LoadAsync(workspaceRoot);
        phases[uuid] = new PhaseData
        {
            Status = ChapterStatus.Drafting,
            PhaseStartDate = "2026-06-01",
            PhaseEndDate = "2026-06-30"
        };
        await new PhaseDataService(store).SaveAsync(workspaceRoot, phases);

        var targets = await new TargetsService(store).LoadAsync(workspaceRoot);
        targets[uuid] = new WordCountTarget { MinWords = 1000 + originalPath.Length, MaxWords = 2000 + originalPath.Length };
        await new TargetsService(store).SaveAsync(workspaceRoot, targets);

        await new WordCountHistoryService(store).AppendAsync(workspaceRoot, uuid, "2026-06-04", 300 + originalPath.Length);

        var notesDirectory = Path.Combine(workspaceRoot, ".hymnal-data", "notes");
        Directory.CreateDirectory(notesDirectory);
        await File.WriteAllTextAsync(Path.Combine(notesDirectory, uuid + ".md"), $"Note for {originalPath}");
    }

    private static async Task AssertUuidSidecarsAsync(string workspaceRoot, string uuid, string originalPath)
    {
        var store = new MetadataStore();
        var phases = await new PhaseDataService(store).LoadAsync(workspaceRoot);
        Assert.Equal(ChapterStatus.Drafting, phases[uuid].Status);
        Assert.Equal("2026-06-01", phases[uuid].PhaseStartDate);
        Assert.Equal("2026-06-30", phases[uuid].PhaseEndDate);

        var targets = await new TargetsService(store).LoadAsync(workspaceRoot);
        Assert.Equal(1000 + originalPath.Length, targets[uuid].MinWords);
        Assert.Equal(2000 + originalPath.Length, targets[uuid].MaxWords);

        var history = await new WordCountHistoryService(store).GetAllAsync(workspaceRoot);
        var entry = Assert.Single(history, item => item.Uuid == uuid);
        Assert.Equal("2026-06-04", entry.Date);
        Assert.Equal(300 + originalPath.Length, entry.WordCount);

        Assert.Equal($"Note for {originalPath}", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, ".hymnal-data", "notes", uuid + ".md")));
    }

    private static void AssertRegistryPath(IReadOnlyDictionary<string, ChapterRegistryEntry> registry, string uuid, string expectedPath) =>
        Assert.Equal(expectedPath, registry[uuid].CurrentPath);

    private static void AssertNoDuplicatePaths(IEnumerable<string> paths, string label)
    {
        var duplicates = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, $"Duplicate {label}: {string.Join(", ", duplicates)}");
    }

    private static IEnumerable<string> GetProjectedPaths(CorkboardViewModel board) =>
        board.Items
            .Where(item => item.Kind is CorkboardItemKind.PartDivider or CorkboardItemKind.ChapterCard)
            .Select(item => item.RelativePath);

    private static PartDividerItemViewModel GetPartDivider(CorkboardViewModel board, string relativePath) =>
        Assert.IsType<PartDividerItemViewModel>(board.Items.First(item => item.RelativePath == relativePath));

    private static async Task<ExcludedChapterCardItemViewModel> WaitForExcludedCardAsync(CorkboardViewModel board, string relativePath)
    {
        await WaitUntilAsync(
            () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard
                                          && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)),
            $"Excluded board card '{relativePath}' did not appear.");
        return Assert.IsType<ExcludedChapterCardItemViewModel>(
            board.Items.First(item => item.Kind == CorkboardItemKind.ExcludedChapterCard
                                      && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));
    }

    private static GanttRowViewModel GetGanttRow(GanttViewModel gantt, string relativePath) =>
        gantt.Rows.Single(row => string.Equals(row.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    private static async Task ExecuteCommandAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));
        await completion.Task;
    }

    private static async Task WaitForVisibleOrderAsync(WorkspaceViewModel workspace, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => workspace.VisibleNodes.Select(node => node.Node.RelativePath).SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase),
            $"Workspace visible nodes did not reach expected order {string.Join(", ", expectedPaths)}; actual order was {string.Join(", ", workspace.VisibleNodes.Select(node => node.Node.RelativePath))}.");
    }

    private static async Task WaitForBoardProjectionAsync(CorkboardViewModel board, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => GetProjectedPaths(board).SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase),
            $"Board projection did not match expected order. Expected: {string.Join(", ", expectedPaths)}. Actual: {string.Join(", ", GetProjectedPaths(board))}.");
    }

    private static async Task WaitForGanttRowsAsync(GanttViewModel gantt, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => gantt.Rows.Select(row => row.RelativePath).SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase),
            $"Gantt rows did not match expected order. Expected: {string.Join(", ", expectedPaths)}. Actual: {string.Join(", ", gantt.Rows.Select(row => row.RelativePath))}.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            if (predicate())
                return;
            await Task.Delay(25);
        }
        Assert.Fail(failureMessage);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly bool _ownsWorkspace;
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
        public TestWorkspaceViewModel Workspace { get; }
        public ChapterRegistryService RegistryService { get; }
        public string WorkspaceRoot { get; }

        public TestContext(string workspaceRoot, RecordingStructureService structureService, bool ownsWorkspace = true)
        {
            WorkspaceRoot = workspaceRoot;
            _ownsWorkspace = ownsWorkspace;
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = new ExclusionManifestService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore));
            RegistryService = new ChapterRegistryService(MetadataStore);
            var history = new WordCountHistoryService(MetadataStore);
            ManuscriptService = new ManuscriptService(NotificationService, ExclusionManifestService);
            structureService.Configure(new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService), ManuscriptService);

            Workspace = new TestWorkspaceViewModel(
                ManuscriptService,
                structureService,
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
                OrphanFileDiscoveryService,
                ReloadWorkspaceFromDiskAsync);
        }

        public CorkboardViewModel CreateBoard() => new(
            Workspace,
            new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService),
            OrphanFileDiscoveryService,
            SettingsStore,
            NotificationService,
            ManuscriptService);

        public GanttViewModel CreateGanttViewModel() => new(Workspace, PhaseDataService, NotificationService);

        public async Task OpenWorkspaceAsync(int expectedNodeCount)
        {
            var result = await ReloadWorkspaceFromDiskAsync();
            Assert.True(result.IsSuccess, result.Error);
            await WaitUntilAsync(
                () => Workspace.HasWorkspace && Workspace.Nodes.Count == expectedNodeCount,
                $"Workspace did not load expected node count {expectedNodeCount}. Actual count: {Workspace.Nodes.Count}. Nodes: {string.Join(", ", Workspace.Nodes.Select(node => node.Node.RelativePath))}");
        }

        public async Task<ChapterViewModel> WaitForNodeAsync(string relativePath, bool requireUuid = false)
        {
            await WaitUntilAsync(
                () => TryGetNode(relativePath, requireUuid, out _),
                $"Workspace node '{relativePath}' did not appear with requireUuid={requireUuid}.");
            TryGetNode(relativePath, requireUuid, out var node);
            return node!;
        }

        public async Task<IReadOnlyList<string>> LoadExcludedPathsAsync()
        {
            var manifest = await ExclusionManifestService.LoadAsync(WorkspaceRoot);
            Assert.True(manifest.IsSuccess, manifest.Error);
            return manifest.Value!.ExcludedPaths;
        }

        private async Task<Result<Unit>> ReloadWorkspaceFromDiskAsync()
        {
            var result = await ManuscriptService.LoadWorkspaceAsync(WorkspaceRoot);
            if (!result.IsSuccess)
                return Result<Unit>.Fail(result.Error!);

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
            Editor.HasWorkspace = true;
            return Result<Unit>.Ok(Unit.Default);
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
            foreach (var node in workspaceNodes.OfType<IDisposable>())
                node.Dispose();
            workspaceNodes.Clear();
            visibleNodes.Clear();
            lookup.Clear();
            foreach (var node in nodes.OrderBy(node => node.Index))
            {
                var uuid = registry.Values.FirstOrDefault(entry => string.Equals(entry.CurrentPath, node.RelativePath, StringComparison.OrdinalIgnoreCase))?.Uuid ?? string.Empty;
                phases.TryGetValue(uuid, out var phaseData);
                var target = string.IsNullOrWhiteSpace(uuid) ? null : TargetsService.GetTarget(targets, uuid);
                var vm = new ChapterViewModel(node, uuid, phaseData, PhaseDataService, TargetsService, SettingsStore, NotificationService, model.WorkspaceRoot, target);
                workspaceNodes.Add(vm);
                visibleNodes.Add(vm);
                lookup[node.RelativePath] = vm;
            }
        }

        private bool TryGetNode(string relativePath, bool requireUuid, out ChapterViewModel? node)
        {
            node = Workspace.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            return node != null && (!requireUuid || !string.IsNullOrWhiteSpace(node.Uuid));
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

        private static IDictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(WorkspaceViewModel workspace, string fieldName) where TKey : notnull
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IDictionary<TKey, TValue>)field.GetValue(workspace)!;
        }

        public sealed class TestWorkspaceViewModel : WorkspaceViewModel
        {
            private readonly Func<Task<Result<Unit>>> _reloadWorkspaceFromDiskAsync;
            public TestWorkspaceViewModel(
                ManuscriptService manuscriptService,
                IBookTxtStructureService structureService,
                IFilePickerService filePicker,
                IAppSettingsStore settingsStore,
                IFolderPickerService folderPicker,
                INotificationService notificationService,
                EditorViewModel editor,
                ChapterRegistryService registryService,
                PhaseDataService phaseDataService,
                TargetsService targetsService,
                WordCountService wordCountService,
                WordCountHistoryService historyService,
                IExclusionManifestService exclusionManifestService,
                IOrphanFileDiscoveryService orphanFileDiscoveryService,
                Func<Task<Result<Unit>>> reloadWorkspaceFromDiskAsync)
                : base(manuscriptService, structureService, filePicker, settingsStore, folderPicker, notificationService, editor, registryService, phaseDataService, targetsService, wordCountService, historyService, exclusionManifestService, orphanFileDiscoveryService)
            {
                _reloadWorkspaceFromDiskAsync = reloadWorkspaceFromDiskAsync;
            }
            public override Task<Result<Unit>> ReloadCurrentWorkspaceAsync() => _reloadWorkspaceFromDiskAsync();
        }

        public void Dispose()
        {
            Editor.Dispose();
            ManuscriptService.Dispose();
            if (!_ownsWorkspace)
                return;
            DeleteWorkspace(WorkspaceRoot);
        }
    }

    private sealed class RecordingStructureService : IBookTxtStructureService
    {
        private IBookTxtStructureService? _inner;
        private ManuscriptService? _manuscriptService;
        public List<ReorderCall> ReorderCalls { get; } = new();
        public void Configure(IBookTxtStructureService inner, ManuscriptService manuscriptService)
        {
            _inner = inner;
            _manuscriptService = manuscriptService;
        }
        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) => Inner.ReadNormalizedEntriesAsync(bookTxtPath);
        public async Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
        {
            ReorderCalls.Add(new ReorderCall(bookTxtPath, chapterPath, newIndex, ReadSuppressCount() > 0));
            return await Inner.ReorderEntryAsync(bookTxtPath, chapterPath, newIndex);
        }
        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath) => Inner.RenameEntryAsync(bookTxtPath, existingPath, replacementPath);
        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) => Inner.AddExistingEntryAsync(bookTxtPath, chapterPath, index);
        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) => Inner.AddExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);
        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) => Inner.IncludeExistingEntryAsync(bookTxtPath, chapterPath, index);
        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) => Inner.IncludeExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);
        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) => Inner.CreateNewChapterAsync(bookTxtPath, chapterPath, content, index);
        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) => Inner.CreateNewPartAsync(bookTxtPath, partPath, title, index);
        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) => Inner.RemoveEntryAsync(bookTxtPath, chapterPath);
        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) => Inner.ExcludeEntryAsync(bookTxtPath, chapterPath);
        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) => Inner.DeleteChapterFileAsync(bookTxtPath, chapterPath);
        private IBookTxtStructureService Inner => _inner ?? throw new InvalidOperationException("Recording structure service was not configured.");
        private int ReadSuppressCount()
        {
            if (_manuscriptService == null) return 0;
            var field = typeof(ManuscriptService).GetField("_suppressCount", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate ManuscriptService watcher suppression field.");
            return (int)field.GetValue(_manuscriptService)!;
        }
    }

    private sealed record ReorderCall(string BookTxtPath, string Path, int NewIndex, bool WatcherWasSuppressed);

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
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);
        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFolderPickerService : IFolderPickerService
    {
        public StubFolderPickerService(string workspaceRoot) => WorkspaceRoot = workspaceRoot;
        public string WorkspaceRoot { get; }
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(WorkspaceRoot);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}
