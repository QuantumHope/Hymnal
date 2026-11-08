using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views;
using ReactiveUI;
using Xunit;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class CorkboardViewModelIntegrationTests
{
    static CorkboardViewModelIntegrationTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task DropCardCommand_CrossPartMove_UpdatesFilesBookTxtRegistryAndReloadedProjection()
    {
        using var context = new TestContext(CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One\n\nBody words."),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two\n\nBody words.")));

        await context.OpenWorkspaceAsync(expectedNodeCount: 4);
        var original = await context.WaitForNodeAsync("part-one/chapter-one.md", requireUuid: true);
        Assert.False(string.IsNullOrWhiteSpace(original.Uuid));
        await SeedUuidSidecarsAsync(context.WorkspaceRoot, original.Uuid);

        using var board = context.CreateBoard();
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-one/chapter-one.md",
            "part-two/part.md",
            "part-two/chapter-two.md");

        var source = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(source));
        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        await context.WaitForNodeAsync("part-two/chapter-one.md", requireUuid: true);
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-two/part.md",
            "part-two/chapter-two.md",
            "part-two/chapter-one.md");

        Assert.Equal(
            new[]
            {
                "part-one/part.md",
                "part-two/part.md",
                "part-two/chapter-two.md",
                "part-two/chapter-one.md"
            },
            ReadBookTxtLines(context.BookTxtPath));

        Assert.False(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-one/chapter-one.md")));
        Assert.True(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-two/chapter-one.md")));

        var moved = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "part-two/chapter-one.md");
        Assert.Equal(original.Uuid, moved.Uuid);
        Assert.Equal(ChapterStatus.Drafting, moved.Status);
        Assert.Equal(1000, moved.Target?.MinWords);
        Assert.Equal(1500, moved.Target?.MaxWords);
        Assert.Equal("part-two/chapter-one.md", board.SelectedCard?.RelativePath);
        Assert.Null(board.LastStructuralError);
        Assert.Empty(context.NotificationService.Errors);

        var registry = await context.RegistryService.LoadAsync(context.WorkspaceRoot);
        Assert.Equal("part-two/chapter-one.md", registry[original.Uuid].CurrentPath);
        await AssertUuidSidecarsAsync(context.WorkspaceRoot, original.Uuid);
    }

    [Fact]
    public async Task DropCardCommand_SamePartReorder_PersistsThroughRealReload()
    {
        using var context = new TestContext(CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md\npart-one/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-one/chapter-three.md", "# Chapter Three")));

        await context.OpenWorkspaceAsync(expectedNodeCount: 4);

        using var board = context.CreateBoard();
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-one/chapter-one.md",
            "part-one/chapter-two.md",
            "part-one/chapter-three.md");

        var source = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(source));
        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-one/part.md",
                AfterRelativePath: "part-one/chapter-three.md")));

        await WaitUntilAsync(
            () => context.Workspace.Nodes.Select(node => node.Node.RelativePath).SequenceEqual(
                new[]
                {
                    "part-one/part.md",
                    "part-one/chapter-two.md",
                    "part-one/chapter-three.md",
                    "part-one/chapter-one.md"
                }),
            $"Workspace nodes did not reorder after same-Part drop. Actual: {string.Join(", ", context.Workspace.Nodes.Select(node => node.Node.RelativePath))}.");

        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-one/chapter-two.md",
            "part-one/chapter-three.md",
            "part-one/chapter-one.md");

        Assert.Equal(
            new[]
            {
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md"
            },
            ReadBookTxtLines(context.BookTxtPath));

        Assert.Equal(
            new[]
            {
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md"
            },
            context.Workspace.Nodes.Select(node => node.Node.RelativePath));

        Assert.Equal("part-one/chapter-one.md", board.SelectedCard?.RelativePath);
        Assert.Null(board.LastStructuralError);
        Assert.Empty(context.NotificationService.Errors);
    }

    [Fact]
    public async Task DropCardCommand_ManifestSaveFailureAfterCommittedMove_ReloadsTruthfulStateAndSurfacesFailure()
    {
        using var context = new TestContext(
            CreateWorkspace(
                ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
                ("part-one/part.md", "{class: part}\n# Part One"),
                ("part-one/chapter-one.md", "# Chapter One\n\nBody words."),
                ("part-two/part.md", "{class: part}\n# Part Two"),
                ("part-two/chapter-two.md", "# Chapter Two\n\nBody words.")),
            exclusionManifestServiceFactory: store => new IncludeFailingManifestService(new ExclusionManifestService(store)));

        await context.OpenWorkspaceAsync(expectedNodeCount: 4);
        var original = await context.WaitForNodeAsync("part-one/chapter-one.md", requireUuid: true);

        using var board = context.CreateBoard();
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-one/chapter-one.md",
            "part-two/part.md",
            "part-two/chapter-two.md");

        var source = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(source));
        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        await context.WaitForNodeAsync("part-two/chapter-one.md", requireUuid: true);
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-two/part.md",
            "part-two/chapter-two.md",
            "part-two/chapter-one.md");

        Assert.Equal(
            new[]
            {
                "part-one/part.md",
                "part-two/part.md",
                "part-two/chapter-two.md",
                "part-two/chapter-one.md"
            },
            ReadBookTxtLines(context.BookTxtPath));

        Assert.False(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-one/chapter-one.md")));
        Assert.True(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-two/chapter-one.md")));
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Contains("manifest save after file move, Book.txt write, and registry update", board.LastStructuralError?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var moved = Assert.Single(context.Workspace.Nodes, node => node.Node.RelativePath == "part-two/chapter-one.md");
        Assert.Equal(original.Uuid, moved.Uuid);

        var registry = await context.RegistryService.LoadAsync(context.WorkspaceRoot);
        Assert.Equal("part-two/chapter-one.md", registry[original.Uuid].CurrentPath);

        var error = Assert.Single(context.NotificationService.Errors);
        Assert.Contains(context.BookTxtPath, error);
        Assert.Contains("manifest save", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DropCardCommand_TargetFileConflict_SurfacesStructuralErrorAndLeavesDiskStateUnchanged()
    {
        using var context = new TestContext(CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"),
            ("part-two/chapter-one.md", "# Conflicting Target")));

        await context.OpenWorkspaceAsync(expectedNodeCount: 4);

        using var board = context.CreateBoard();
        await WaitForBoardProjectionAsync(board,
            "part-one/part.md",
            "part-one/chapter-one.md",
            "part-two/part.md",
            "part-two/chapter-two.md");

        var beforeProjection = GetProjectedPaths(board).ToArray();
        var source = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(source));

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        Assert.Equal(beforeProjection, GetProjectedPaths(board).ToArray());
        Assert.Equal(
            new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-two/part.md",
                "part-two/chapter-two.md"
            },
            ReadBookTxtLines(context.BookTxtPath));
        Assert.True(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-one/chapter-one.md")));
        Assert.True(File.Exists(AbsolutePath(context.WorkspaceRoot, "part-two/chapter-one.md")));
        Assert.Equal("part-one/chapter-one.md", board.SelectedCard?.RelativePath);
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Contains("target file", board.LastStructuralError?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var error = Assert.Single(context.NotificationService.Errors);
        Assert.Contains(context.BookTxtPath, error);
        Assert.Contains("target file", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveFromBookCommand_ExcludesCardAndReloadedBoardProjectsNonDraggableExcludedCard()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            await context.OpenWorkspaceAsync(expectedNodeCount: 3);

            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md");

            var chapterTwo = GetChapterCard(board, "part-one/chapter-two.md");
            await ExecuteCommandAsync(board.SelectCardCommand.Execute(chapterTwo));
            await ExecuteCommandAsync(board.RemoveFromBookCommand.Execute(new RemoveChapterRequest("part-one/chapter-two.md")));

            await WaitUntilAsync(
                () => context.Workspace.Nodes.Count == 3
                      && context.Workspace.Nodes.Any(node => node.Node.RelativePath == "part-one/chapter-two.md" && node.Node.IsExcluded),
                "Workspace did not reproject the excluded chapter after removal.");
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md");

            Assert.Equal(
                new[]
                {
                    "part-one/part.md",
                    "part-one/chapter-one.md"
                },
                ReadBookTxtLines(context.BookTxtPath));
            Assert.Equal(new[] { "part-one/chapter-two.md" }, await context.LoadExcludedPathsAsync());
            Assert.DoesNotContain(board.Items, item => item.Kind == CorkboardItemKind.ChapterCard && item.RelativePath == "part-one/chapter-two.md");

            var excluded = await WaitForExcludedCardAsync(board, "part-one/chapter-two.md");
            Assert.Equal("(excluded)", excluded.ExcludedLabel);
            Assert.False(CorkboardView.CanDragFromCorkboard(excluded));
            Assert.Null(board.SelectedCard);

            await ExecuteCommandAsync(board.OpenCardCommand.Execute(excluded));
            Assert.Null(board.SelectedCard);
            Assert.Null(board.LastStructuralError);
            Assert.Empty(context.NotificationService.Errors);

            using var reloadedContext = new TestContext(workspaceRoot, ownsWorkspace: false);
            await reloadedContext.OpenWorkspaceAsync(expectedNodeCount: 3);

            using var reloadedBoard = reloadedContext.CreateBoard();
            await WaitForBoardProjectionAsync(reloadedBoard,
                "part-one/part.md",
                "part-one/chapter-one.md");
            await WaitForExcludedCardAsync(reloadedBoard, "part-one/chapter-two.md");
            Assert.DoesNotContain(reloadedBoard.Items, item => item.Kind == CorkboardItemKind.ChapterCard && item.RelativePath == "part-one/chapter-two.md");
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_ReincludesExcludedCardAtRequestedIndexAndPersistsFreshReload()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-one/chapter-three.md", "# Chapter Three"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspaceRoot, "part-one/chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);

            await context.OpenWorkspaceAsync(expectedNodeCount: 4);
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-three.md");
            await WaitForExcludedCardAsync(board, "part-one/chapter-two.md");

            await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
                new IncludeExistingChapterRequest(
                    "part-one/chapter-two.md",
                    board.GetInsertIndexAfterChapter("part-one/chapter-one.md"))));

            await WaitUntilAsync(
                () => context.Workspace.Nodes.Select(node => node.Node.RelativePath).SequenceEqual(
                    new[]
                    {
                        "part-one/part.md",
                        "part-one/chapter-one.md",
                        "part-one/chapter-two.md",
                        "part-one/chapter-three.md"
                    }),
                $"Workspace did not reorder included nodes correctly. Actual: {string.Join(", ", context.Workspace.Nodes.Select(node => node.Node.RelativePath))}.");
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md");

            Assert.Equal(
                new[]
                {
                    "part-one/part.md",
                    "part-one/chapter-one.md",
                    "part-one/chapter-two.md",
                    "part-one/chapter-three.md"
                },
                ReadBookTxtLines(context.BookTxtPath));
            Assert.Empty(await context.LoadExcludedPathsAsync());
            Assert.DoesNotContain(board.Items, item => item.Kind == CorkboardItemKind.ExcludedChapterCard && item.RelativePath == "part-one/chapter-two.md");
            Assert.IsType<ChapterCardItemViewModel>(board.Items.Single(item => item.RelativePath == "part-one/chapter-two.md"));
            Assert.Null(board.LastStructuralError);
            Assert.Empty(context.NotificationService.Errors);

            using var reloadedContext = new TestContext(workspaceRoot, ownsWorkspace: false);
            await reloadedContext.OpenWorkspaceAsync(expectedNodeCount: 4);
            using var reloadedBoard = reloadedContext.CreateBoard();
            await WaitForBoardProjectionAsync(reloadedBoard,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md");
            Assert.IsType<ChapterCardItemViewModel>(reloadedBoard.Items.Single(item => item.RelativePath == "part-one/chapter-two.md"));
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public async Task CommitInlineCreateAsync_CreatesChapterBetweenCardsAndPreservesSingleUuidAcrossFreshReload()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-three.md", "# Chapter Three"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            await context.OpenWorkspaceAsync(expectedNodeCount: 3);
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-three.md");

            var owningPart = GetPartDivider(board, "part-one/part.md");
            board.BeginInlineCreate(board.GetInsertIndexAfterChapter("part-one/chapter-one.md"), owningPart);
            await board.CommitInlineCreateAsync("Chapter Two");

            await context.WaitForNodeAsync("part-one/chapter-two.md", requireUuid: true);
            await WaitForBoardProjectionAsync(board,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md");

            Assert.Equal("# Chapter Two\n\n", await File.ReadAllTextAsync(AbsolutePath(workspaceRoot, "part-one/chapter-two.md")));
            Assert.Equal(
                new[]
                {
                    "part-one/part.md",
                    "part-one/chapter-one.md",
                    "part-one/chapter-two.md",
                    "part-one/chapter-three.md"
                },
                ReadBookTxtLines(context.BookTxtPath));

            var registry = await context.RegistryService.LoadAsync(workspaceRoot);
            var firstUuid = Assert.Single(registry.Values, entry => entry.CurrentPath == "part-one/chapter-two.md").Uuid;
            Assert.False(string.IsNullOrWhiteSpace(firstUuid));
            Assert.Null(board.LastStructuralError);
            Assert.Empty(context.NotificationService.Errors);

            using var reloadedContext = new TestContext(workspaceRoot, ownsWorkspace: false);
            await reloadedContext.OpenWorkspaceAsync(expectedNodeCount: 4);
            var reloadedNode = await reloadedContext.WaitForNodeAsync("part-one/chapter-two.md", requireUuid: true);
            Assert.Equal(firstUuid, reloadedNode.Uuid);

            var reloadedRegistry = await reloadedContext.RegistryService.LoadAsync(workspaceRoot);
            Assert.Equal(firstUuid, Assert.Single(reloadedRegistry.Values, entry => entry.CurrentPath == "part-one/chapter-two.md").Uuid);

            using var reloadedBoard = reloadedContext.CreateBoard();
            await WaitForBoardProjectionAsync(reloadedBoard,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md");
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public async Task CommitInlineCreateAsync_AroundPartDividers_UsesEmptyAndNestedPartFolderSemantics()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "intro.md\npart-empty/part.md\npart-two/part.md\npart-two/act-one/part.md\npart-two/act-one/chapter-one.md"),
            ("intro.md", "# Intro"),
            ("part-empty/part.md", "{class: part}\n# Part Empty"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/act-one/part.md", "{class: part}\n# Act One"),
            ("part-two/act-one/chapter-one.md", "# Chapter One"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            await context.OpenWorkspaceAsync(expectedNodeCount: 5);
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "intro.md",
                "part-empty/part.md",
                "part-two/part.md",
                "part-two/act-one/part.md",
                "part-two/act-one/chapter-one.md");

            var emptyPart = GetPartDivider(board, "part-empty/part.md");
            board.BeginInlineCreate(board.GetInsertIndexAfterPart(emptyPart.RelativePath), emptyPart);
            await board.CommitInlineCreateAsync("Inserted Empty");

            var nestedPart = GetPartDivider(board, "part-two/act-one/part.md");
            board.BeginInlineCreate(board.GetInsertIndexAfterPart(nestedPart.RelativePath), nestedPart);
            await board.CommitInlineCreateAsync("Nested Scene");

            await context.WaitForNodeAsync("part-empty/inserted-empty.md", requireUuid: true);
            await context.WaitForNodeAsync("part-two/act-one/nested-scene.md", requireUuid: true);
            await WaitForBoardProjectionAsync(board,
                "intro.md",
                "part-empty/part.md",
                "part-empty/inserted-empty.md",
                "part-two/part.md",
                "part-two/act-one/part.md",
                "part-two/act-one/chapter-one.md",
                "part-two/act-one/nested-scene.md");

            Assert.True(File.Exists(AbsolutePath(workspaceRoot, "part-empty/inserted-empty.md")));
            Assert.True(File.Exists(AbsolutePath(workspaceRoot, "part-two/act-one/nested-scene.md")));
            Assert.Equal("# Inserted Empty\n\n", await File.ReadAllTextAsync(AbsolutePath(workspaceRoot, "part-empty/inserted-empty.md")));
            Assert.Equal("# Nested Scene\n\n", await File.ReadAllTextAsync(AbsolutePath(workspaceRoot, "part-two/act-one/nested-scene.md")));
            Assert.Equal(
                new[]
                {
                    "intro.md",
                    "part-empty/part.md",
                    "part-empty/inserted-empty.md",
                    "part-two/part.md",
                    "part-two/act-one/part.md",
                    "part-two/act-one/chapter-one.md",
                    "part-two/act-one/nested-scene.md"
                },
                ReadBookTxtLines(context.BookTxtPath));

            using var reloadedContext = new TestContext(workspaceRoot, ownsWorkspace: false);
            await reloadedContext.OpenWorkspaceAsync(expectedNodeCount: 7);
            using var reloadedBoard = reloadedContext.CreateBoard();
            await WaitForBoardProjectionAsync(reloadedBoard,
                "intro.md",
                "part-empty/part.md",
                "part-empty/inserted-empty.md",
                "part-two/part.md",
                "part-two/act-one/part.md",
                "part-two/act-one/chapter-one.md",
                "part-two/act-one/nested-scene.md");
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public async Task CommitInlineCreateAsync_DuplicatePathFailure_LeavesBookFilesAndRegistryRecoverable()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "part-two/part.md\npart-two/act-one/part.md\npart-two/act-one/scene-1.md"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/act-one/part.md", "{class: part}\n# Act One"),
            ("part-two/act-one/scene-1.md", "# Scene 1"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            await context.OpenWorkspaceAsync(expectedNodeCount: 3);
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board,
                "part-two/part.md",
                "part-two/act-one/part.md",
                "part-two/act-one/scene-1.md");

            var beforeBook = ReadBookTxtLines(context.BookTxtPath);
            var beforeRegistry = await context.RegistryService.LoadAsync(workspaceRoot);
            var nestedPart = GetPartDivider(board, "part-two/act-one/part.md");

            board.BeginInlineCreate(board.GetInsertIndexAfterPart(nestedPart.RelativePath), nestedPart);
            await board.CommitInlineCreateAsync("Scene 1");

            await WaitForBoardProjectionAsync(board,
                "part-two/part.md",
                "part-two/act-one/part.md",
                "part-two/act-one/scene-1.md");

            Assert.Equal(beforeBook, ReadBookTxtLines(context.BookTxtPath));
            Assert.True(File.Exists(AbsolutePath(workspaceRoot, "part-two/act-one/scene-1.md")));
            Assert.Equal(beforeRegistry.Count, (await context.RegistryService.LoadAsync(workspaceRoot)).Count);
            Assert.DoesNotContain(board.Items, item => item.Kind == CorkboardItemKind.InlineCreate);
            Assert.Equal("Create chapter", board.LastStructuralError?.Operation);
            Assert.Equal("part-two/act-one/scene-1.md", board.LastStructuralError?.Path);
            Assert.Equal(context.BookTxtPath, board.LastStructuralError?.BookTxtPath);
            Assert.Contains("already exists", board.LastStructuralError?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains(context.BookTxtPath, error);
            Assert.Contains("already exists", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_InvalidPathFailure_LeavesBookManifestRegistryAndProjectionRecoverable()
    {
        var workspaceRoot = CreateWorkspace(
            ("Book.txt", "chapter-one.md"),
            ("chapter-one.md", "# Chapter One"),
            ("chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new TestContext(workspaceRoot, ownsWorkspace: false);
            var excluded = await context.ExclusionManifestService.ExcludeAsync(workspaceRoot, "chapter-two.md");
            Assert.True(excluded.IsSuccess, excluded.Error);

            await context.OpenWorkspaceAsync(expectedNodeCount: 2);
            using var board = context.CreateBoard();
            await WaitForBoardProjectionAsync(board, "chapter-one.md");
            await WaitForExcludedCardAsync(board, "chapter-two.md");

            var beforeBook = ReadBookTxtLines(context.BookTxtPath);
            var beforeManifest = await context.LoadExcludedPathsAsync();
            var beforeRegistry = await context.RegistryService.LoadAsync(workspaceRoot);
            var beforeProjection = board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray();

            await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
                new IncludeExistingChapterRequest("../escape.md", board.GetBookInsertIndex())));

            Assert.Equal(beforeBook, ReadBookTxtLines(context.BookTxtPath));
            Assert.Equal(beforeManifest, await context.LoadExcludedPathsAsync());
            Assert.Equal(beforeRegistry.Count, (await context.RegistryService.LoadAsync(workspaceRoot)).Count);
            Assert.Equal(beforeProjection, board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray());
            Assert.Equal("Include chapter", board.LastStructuralError?.Operation);
            Assert.Equal("../escape.md", board.LastStructuralError?.Path);
            Assert.Equal(context.BookTxtPath, board.LastStructuralError?.BookTxtPath);
            Assert.Contains("outside the manuscript root", board.LastStructuralError?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains(context.BookTxtPath, error);
            Assert.Contains("outside the manuscript root", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    private static string CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-corkboard-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
            WriteWorkspaceFile(root, relativePath, content);

        return root;
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

    private static IEnumerable<string> GetProjectedPaths(CorkboardViewModel board) =>
        board.Items
            .Where(item => item.Kind is CorkboardItemKind.PartDivider or CorkboardItemKind.ChapterCard)
            .Select(item => item.RelativePath);

    private static ChapterCardItemViewModel GetChapterCard(CorkboardViewModel board, string relativePath) =>
        Assert.IsType<ChapterCardItemViewModel>(board.Items.First(item => item.RelativePath == relativePath));

    private static PartDividerItemViewModel GetPartDivider(CorkboardViewModel board, string relativePath) =>
        Assert.IsType<PartDividerItemViewModel>(board.Items.First(item => item.RelativePath == relativePath));

    private static async Task<ExcludedChapterCardItemViewModel> WaitForExcludedCardAsync(CorkboardViewModel board, string relativePath)
    {
        await WaitUntilAsync(
            () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard
                                          && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)),
            $"Excluded board card '{relativePath}' did not appear.");

        return Assert.IsType<ExcludedChapterCardItemViewModel>(
            board.Items.Single(item => item.Kind == CorkboardItemKind.ExcludedChapterCard
                                       && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));
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

    private static async Task WaitForBoardProjectionAsync(CorkboardViewModel board, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => GetProjectedPaths(board).SequenceEqual(expectedPaths),
            $"Board projection did not match expected order. Expected: {string.Join(", ", expectedPaths)}. Actual: {string.Join(", ", GetProjectedPaths(board))}.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage, Func<Task>? settleAsync = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !predicate())
        {
            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            if (settleAsync != null)
                await settleAsync();
            await Task.Delay(25);
        }

        if (settleAsync != null)
            await settleAsync();

        if (!predicate())
            Assert.Fail(failureMessage);

        ReactiveUiTestBootstrap.RunOnUiThread(() => { });
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
        public IExclusionManifestService ExclusionManifestService { get; }
        public ManuscriptService ManuscriptService { get; }
        public OrphanFileDiscoveryService OrphanFileDiscoveryService { get; } = new();
        public EditorViewModel Editor { get; }
        public TestWorkspaceViewModel Workspace { get; }
        public ChapterRegistryService RegistryService { get; }

        public string WorkspaceRoot { get; }
        public string BookTxtPath => Path.Combine(WorkspaceRoot, "Book.txt");

        public TestContext(
            string workspaceRoot,
            Func<IMetadataStore, IExclusionManifestService>? exclusionManifestServiceFactory = null,
            bool ownsWorkspace = true)
        {
            WorkspaceRoot = workspaceRoot;
            _ownsWorkspace = ownsWorkspace;
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = exclusionManifestServiceFactory?.Invoke(MetadataStore)
                ?? new ExclusionManifestService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore));
            RegistryService = new ChapterRegistryService(MetadataStore);
            var history = new WordCountHistoryService(MetadataStore);
            ManuscriptService = new ManuscriptService(NotificationService, ExclusionManifestService);
            var structure = new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService);

            Workspace = new TestWorkspaceViewModel(
                ManuscriptService,
                structure,
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

        public async Task OpenWorkspaceAsync(int expectedNodeCount)
        {
            var result = await ReloadWorkspaceFromDiskAsync();
            Assert.True(result.IsSuccess, result.Error);

            await WaitUntilAsync(
                () => Workspace.HasWorkspace && Workspace.Nodes.Count == expectedNodeCount,
                $"Workspace did not load expected node count {expectedNodeCount}. Actual count: {Workspace.Nodes.Count}.");
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

        public async Task<ChapterViewModel> WaitForNodeAsync(string relativePath, bool requireUuid = false)
        {
            await WaitUntilAsync(
                () => TryGetNode(relativePath, requireUuid, out _),
                $"Workspace node '{relativePath}' did not appear with requireUuid={requireUuid}.");

            TryGetNode(relativePath, requireUuid, out var node);
            return node!;
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
                var vm = new ChapterViewModel(
                    node,
                    uuid,
                    phaseData,
                    TestMetadataStores.Sidecar(PhaseDataService, TargetsService),
                    SettingsStore,
                    NotificationService,
                    model.WorkspaceRoot,
                    target);
                workspaceNodes.Add(vm);
                visibleNodes.Add(vm);
                lookup[node.RelativePath] = vm;
            }
        }

        private bool TryGetNode(string relativePath, bool requireUuid, out ChapterViewModel? node)
        {
            node = Workspace.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

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

        private static IDictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(WorkspaceViewModel workspace, string fieldName)
            where TKey : notnull
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IDictionary<TKey, TValue>)field.GetValue(workspace)!;
        }

        public async Task<IReadOnlyList<string>> LoadExcludedPathsAsync()
        {
            var manifest = await ExclusionManifestService.LoadAsync(WorkspaceRoot);
            Assert.True(manifest.IsSuccess, manifest.Error);
            return manifest.Value!.ExcludedPaths;
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
                : base(
                    manuscriptService,
                    structureService,
                    filePicker,
                    settingsStore,
                    folderPicker,
                    notificationService,
                    editor,
                    registryService,
                    phaseDataService,
                    targetsService,
                    wordCountService,
                    historyService,
                    exclusionManifestService,
                    orphanFileDiscoveryService)
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

            try
            {
                if (Directory.Exists(WorkspaceRoot))
                    Directory.Delete(WorkspaceRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private sealed class IncludeFailingManifestService : IExclusionManifestService
    {
        private readonly IExclusionManifestService _inner;

        public IncludeFailingManifestService(IExclusionManifestService inner) => _inner = inner;

        public Task<Result<ExclusionManifest>> LoadAsync(string workspaceRoot) => _inner.LoadAsync(workspaceRoot);

        public Task<Result<ExclusionManifest>> SaveAsync(string workspaceRoot, ExclusionManifest manifest)
            => _inner.SaveAsync(workspaceRoot, manifest);

        public Task<Result<ExclusionManifest>> ExcludeAsync(string workspaceRoot, string relativePath)
            => _inner.ExcludeAsync(workspaceRoot, relativePath);

        public Task<Result<ExclusionManifest>> IncludeAsync(string workspaceRoot, string relativePath)
            => Task.FromResult(Result<ExclusionManifest>.Fail("simulated manifest save failure"));
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

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(_workspaceRoot);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}
