using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Unit = Hymnal.Core.Common.Unit;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using DynamicData.Binding;
using Hymnal.Core.Tests.Infrastructure;
using ReactiveUI;
using Xunit;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class CorkboardViewModelTests
{
    static CorkboardViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public void Ctor_ProjectsMixedItemsInBookOrder()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        Assert.Equal(
            new[]
            {
                CorkboardItemKind.PartDivider,
                CorkboardItemKind.ChapterCard,
                CorkboardItemKind.PartDivider,
                CorkboardItemKind.ChapterCard
            },
            board.Items.Select(item => item.Kind));

        Assert.Equal(
            new[] { "part-one/part.md", "part-one/chapter-one.md", "part-two/part.md", "part-two/chapter-two.md" },
            board.Items.Select(item => item.RelativePath));
    }

    [Fact]
    public void Ctor_IncludesEmptyPartHintsAndEmptyWorkspaceHasNoItems()
    {
        using var context = CreateContext();

        using var emptyBoard = context.CreateCorkboard();
        Assert.Empty(emptyBoard.Items);
        Assert.Null(emptyBoard.SelectedCard);

        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part));

        using var board = context.CreateCorkboard();

        Assert.Equal(
            new[]
            {
                CorkboardItemKind.PartDivider,
                CorkboardItemKind.ChapterCard,
                CorkboardItemKind.PartDivider,
                CorkboardItemKind.EmptyPartHint
            },
            board.Items.Select(item => item.Kind));
    }

    [Fact]
    public void WorkspaceNodeCollectionChanges_RebuildBoardLive()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        Assert.Equal(2, board.Items.Count);

        AddWorkspaceNode(context, CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        Assert.True(
            SpinWait.SpinUntil(() => board.Items.Count == 3, TimeSpan.FromSeconds(2)),
            "Corkboard items did not rebuild after workspace node collection change.");
        Assert.Contains(board.Items, item => item.RelativePath == "part-one/chapter-two.md");
    }

    [Fact]
    public async Task SelectCardCommand_EnforcesSelectionExclusivity()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var first = GetChapterCard(board, "part-one/chapter-one.md");
        var second = GetChapterCard(board, "part-one/chapter-two.md");

        await ExecuteCommandAsync(board.SelectCardCommand.Execute(first));
        Assert.Same(first.Card, board.SelectedCard);
        Assert.True(first.Card.IsSelected);
        Assert.False(second.Card.IsSelected);

        await ExecuteCommandAsync(board.SelectCardCommand.Execute(second));
        Assert.Same(second.Card, board.SelectedCard);
        Assert.False(first.Card.IsSelected);
        Assert.True(second.Card.IsSelected);
    }

    [Fact]
    public async Task SelectCardCommand_IgnoresPartItems()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        var chapter = GetChapterCard(board, "part-one/chapter-one.md");
        var part = Assert.IsType<PartDividerItemViewModel>(board.Items[0]);

        await ExecuteCommandAsync(board.SelectCardCommand.Execute(chapter));
        Assert.Same(chapter.Card, board.SelectedCard);

        await ExecuteCommandAsync(board.SelectCardCommand.Execute(part));
        Assert.Same(chapter.Card, board.SelectedCard);
        Assert.True(chapter.Card.IsSelected);
    }

    [Fact]
    public void OpenSelectedCardCommand_WithNoSelection_DoesNotEmit()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        var emitted = 0;
        using var sub = board.OpenChapterRequested.Subscribe(_ => emitted++);

        Assert.False(WaitForFirstCanExecute(board.OpenSelectedCardCommand));

        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task OpenSelectedCardCommand_EmitsSelectedChapter()
    {
        using var context = CreateContext();
        var chapterNode = CreateChapter(context, "part-one/chapter-one.md", "Chapter One");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            chapterNode,
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        ChapterViewModel? opened = null;
        using var sub = board.OpenChapterRequested.Subscribe(chapter => opened = chapter);

        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));
        await ExecuteCommandAsync(board.OpenSelectedCardCommand.Execute());

        Assert.Same(card.Card.Chapter, opened);
        Assert.Same(card.Card, board.SelectedCard);
    }

    [Fact]
    public async Task OpenCardCommand_EmitsSpecificChapter()
    {
        using var context = CreateContext();
        var chapterNode = CreateChapter(context, "part-one/chapter-one.md", "Chapter One");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            chapterNode,
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-two.md");
        ChapterViewModel? opened = null;
        using var sub = board.OpenChapterRequested.Subscribe(chapter => opened = chapter);

        await ExecuteCommandAsync(board.OpenCardCommand.Execute(card));

        Assert.Same(card.Card.Chapter, opened);
        Assert.Same(card.Card, board.SelectedCard);
    }

    [Fact]
    public async Task ReorderCardCommand_CallsStructureServiceAndReorders()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"),
            CreateChapter(context, "part-one/chapter-three.md", "Chapter Three"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));

        await ExecuteCommandAsync(board.ReorderCardCommand.Execute(
            new ReorderCardRequest("part-one/chapter-one.md", AfterRelativePath: "part-one/chapter-three.md")));

        Assert.Single(context.StructureService.ReorderCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", 3), context.StructureService.ReorderCalls[0]);
        Assert.Equal(1, context.Workspace.ReorderCount);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Same(card.Card, board.SelectedCard);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task DropCardCommand_SamePartAfterSibling_ReordersAtAllNodeBookTxtIndex()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"),
            CreateChapter(context, "part-one/chapter-three.md", "Chapter Three"));

        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-one/part.md",
                AfterRelativePath: "part-one/chapter-three.md")));

        Assert.Single(context.StructureService.ReorderCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", 3), context.StructureService.ReorderCalls[0]);
        Assert.Empty(context.StructureService.MoveCalls);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Equal(0, context.Workspace.ReorderCount);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task DropCardCommand_CrossPartAfterSibling_MovesFileToTargetPartFolder()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        Assert.Empty(context.StructureService.ReorderCalls);
        var move = Assert.Single(context.StructureService.MoveCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", "part-two/chapter-one.md", 3), move);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task DropCardCommand_CrossPartIntoNestedPart_UsesFullTargetFolderPath()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/act-one/part.md", "Act One", NodeKind.Part),
            CreateChapter(context, "part-two/act-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/act-one/part.md",
                AfterRelativePath: "part-two/act-one/chapter-two.md")));

        Assert.Empty(context.StructureService.ReorderCalls);
        var move = Assert.Single(context.StructureService.MoveCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", "part-two/act-one/chapter-one.md", 4), move);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task DropCardCommand_IntoEmptyPart_MovesToSlotImmediatelyAfterPartDivider()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part));

        using var board = context.CreateCorkboard();
        Assert.Contains(board.Items, item => item.Kind == CorkboardItemKind.EmptyPartHint && item.RelativePath == "part-two/part.md");

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md")));

        Assert.Empty(context.StructureService.ReorderCalls);
        var move = Assert.Single(context.StructureService.MoveCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", "part-two/chapter-one.md", 3), move);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task DropCardCommand_InvalidMissingExcludedAndSelfDrops_DoNotCallCoreAndSurfaceStructuralError()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.OrphanDiscovery.Orphans = new[]
        {
            new OrphanFileInfo("part-one/excluded.md", "Excluded", "part-one")
        };
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard && item.RelativePath == "part-one/excluded.md"),
                TimeSpan.FromSeconds(2)),
            "Excluded card was not projected before invalid-drop contract assertions.");

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest("missing.md", TargetPartPath: "part-one/part.md")));
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("missing.md", board.LastStructuralError?.Path);
        Assert.Contains("not on the current board", board.LastStructuralError?.Message ?? string.Empty);

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest("part-one/excluded.md", TargetPartPath: "part-one/part.md")));
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/excluded.md", board.LastStructuralError?.Path);
        Assert.Contains("excluded", (board.LastStructuralError?.Message ?? string.Empty).ToLowerInvariant());

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-one/part.md",
                AfterRelativePath: "part-one/chapter-one.md")));
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Contains("itself", (board.LastStructuralError?.Message ?? string.Empty).ToLowerInvariant());

        Assert.Empty(context.StructureService.ReorderCalls);
        Assert.Empty(context.StructureService.MoveCalls);
        Assert.Equal(3, context.NotificationService.Errors.Count);
        Assert.All(context.NotificationService.Errors, error => Assert.Contains(context.Workspace.BookTxtPath, error));
    }

    [Fact]
    public async Task DropCardCommand_MoveFailure_LeavesProjectionIntactAndSurfacesStructuralError()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.StructureService.MoveResult = Result<Unit>.Fail("move failed on disk");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));
        var itemsBefore = board.Items.Select(item => item.RelativePath).ToArray();

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        var move = Assert.Single(context.StructureService.MoveCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md", "part-two/chapter-one.md", 3), move);
        Assert.Empty(context.StructureService.ReorderCalls);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Equal(itemsBefore, board.Items.Select(item => item.RelativePath));
        Assert.Same(card.Card, board.SelectedCard);
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Equal("move failed on disk", board.LastStructuralError?.Message);
        Assert.Contains("move failed on disk", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public async Task DropCardCommand_ReloadFailure_SurfacesStructuralErrorAfterSuccessfulMove()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.Workspace.ReloadResult = Result<Unit>.Fail("reload failed after move");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.DropCardCommand.Execute(
            new CorkboardDropRequest(
                "part-one/chapter-one.md",
                TargetPartPath: "part-two/part.md",
                AfterRelativePath: "part-two/chapter-two.md")));

        Assert.Single(context.StructureService.MoveCalls);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Equal("Drop card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Equal("reload failed after move", board.LastStructuralError?.Message);
        Assert.Contains("reload failed after move", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public async Task RemoveFromBookCommand_UsesCanonicalExcludeReloadsAndClearsSelectionWhenCardBecomesExcluded()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.OrphanDiscovery.Orphans = new[]
        {
            new OrphanFileInfo("part-one/chapter-one.md", "Chapter One", "part-one")
        };
        var part = CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part);
        var chapter = CreateChapter(context, "part-one/chapter-one.md", "Chapter One");
        SeedWorkspaceNodes(context, part, chapter);

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));
        context.Workspace.OnReload = () => SeedWorkspaceNodes(context, part);

        await ExecuteCommandAsync(board.RemoveFromBookCommand.Execute(
            new RemoveChapterRequest("part-one/chapter-one.md")));

        var exclude = Assert.Single(context.StructureService.ExcludeCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/chapter-one.md"), exclude);
        Assert.Empty(context.StructureService.RemoveCalls);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
        Assert.Null(board.SelectedCard);
        Assert.False(card.Card.IsSelected);
        Assert.Contains(board.Items, item =>
            item.Kind == CorkboardItemKind.ExcludedChapterCard &&
            item.RelativePath == "part-one/chapter-one.md");
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_WithIndex_UsesCanonicalIncludeAndReloads()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.OrphanDiscovery.Orphans = new[]
        {
            new OrphanFileInfo("part-one/excluded.md", "Excluded", "part-one")
        };
        var part = CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part);
        SeedWorkspaceNodes(context, part);

        using var board = context.CreateCorkboard();
        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard && item.RelativePath == "part-one/excluded.md"),
                TimeSpan.FromSeconds(2)),
            "Excluded card was not projected before include contract assertion.");
        context.Workspace.OnReload = () =>
        {
            context.OrphanDiscovery.Orphans = Array.Empty<OrphanFileInfo>();
            SeedWorkspaceNodes(context, part, CreateChapter(context, "part-one/excluded.md", "Excluded"));
        };

        await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
            new IncludeExistingChapterRequest("part-one/excluded.md", Index: 1)));

        var include = Assert.Single(context.StructureService.IncludeExistingIndexCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/excluded.md", 1), include);
        Assert.Empty(context.StructureService.AddExistingIndexCalls);
        Assert.Empty(context.StructureService.IncludeExistingPartCalls);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item =>
                    item.Kind == CorkboardItemKind.ChapterCard &&
                    item.RelativePath == "part-one/excluded.md"),
                TimeSpan.FromSeconds(2)),
            "Included card was not projected after workspace reload.");
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_WithPartPath_UsesCanonicalIncludeAfterPartAndReloads()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.OrphanDiscovery.Orphans = new[]
        {
            new OrphanFileInfo("part-one/excluded.md", "Excluded", "part-one")
        };
        var part = CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part);
        SeedWorkspaceNodes(context, part);

        using var board = context.CreateCorkboard();
        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard && item.RelativePath == "part-one/excluded.md"),
                TimeSpan.FromSeconds(2)),
            "Excluded card was not projected before include-after-part contract assertion.");
        context.Workspace.OnReload = () =>
        {
            context.OrphanDiscovery.Orphans = Array.Empty<OrphanFileInfo>();
            SeedWorkspaceNodes(context, part, CreateChapter(context, "part-one/excluded.md", "Excluded"));
        };

        await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
            new IncludeExistingChapterRequest("part-one/excluded.md", PartPath: "part-one/part.md")));

        var include = Assert.Single(context.StructureService.IncludeExistingPartCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/excluded.md", "part-one/part.md"), include);
        Assert.Empty(context.StructureService.AddExistingPartCalls);
        Assert.Empty(context.StructureService.IncludeExistingIndexCalls);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_WithoutIndexOrPartPath_FailsLocallyWithoutCallingCore()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part));

        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
            new IncludeExistingChapterRequest("part-one/excluded.md")));

        Assert.Empty(context.StructureService.IncludeExistingIndexCalls);
        Assert.Empty(context.StructureService.IncludeExistingPartCalls);
        Assert.Empty(context.StructureService.AddExistingIndexCalls);
        Assert.Empty(context.StructureService.AddExistingPartCalls);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Equal("Include chapter", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/excluded.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Contains("requires either an index or a part path", board.LastStructuralError?.Message ?? string.Empty);
        Assert.Contains("requires either an index or a part path", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public async Task IncludeExistingChapterCommand_CoreFailure_LeavesProjectionIntactAndSurfacesStructuralError()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.StructureService.IncludeExistingIndexResult = Result<Unit>.Fail("include denied by structure service");
        context.OrphanDiscovery.Orphans = new[]
        {
            new OrphanFileInfo("part-one/excluded.md", "Excluded", "part-one")
        };
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part));

        using var board = context.CreateCorkboard();
        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item => item.Kind == CorkboardItemKind.ExcludedChapterCard && item.RelativePath == "part-one/excluded.md"),
                TimeSpan.FromSeconds(2)),
            "Excluded card was not projected before include failure assertion.");
        var itemsBefore = board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray();

        await ExecuteCommandAsync(board.IncludeExistingChapterCommand.Execute(
            new IncludeExistingChapterRequest("part-one/excluded.md", Index: 1)));

        Assert.Single(context.StructureService.IncludeExistingIndexCalls);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Equal(itemsBefore, board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray());
        Assert.Equal("Include chapter", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/excluded.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Equal("include denied by structure service", board.LastStructuralError?.Message);
        Assert.Contains("include denied by structure service", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public async Task RemoveFromBookCommand_CoreFailure_LeavesProjectionIntactAndSurfacesStructuralError()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.StructureService.ExcludeResult = Result<Unit>.Fail("exclude denied by structure service");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));
        var itemsBefore = board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray();

        await ExecuteCommandAsync(board.RemoveFromBookCommand.Execute(
            new RemoveChapterRequest("part-one/chapter-one.md")));

        Assert.Single(context.StructureService.ExcludeCalls);
        Assert.Empty(context.StructureService.RemoveCalls);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Equal(itemsBefore, board.Items.Select(item => (item.Kind, item.RelativePath)).ToArray());
        Assert.Same(card.Card, board.SelectedCard);
        Assert.Equal("Remove from book", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Equal("exclude denied by structure service", board.LastStructuralError?.Message);
        Assert.Contains("exclude denied by structure service", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public async Task StructuralFailure_LeavesSelectionIntactAndSurfacesError()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.StructureService.ReorderResult = Result<Unit>.Fail("disk write exploded");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));
        var itemsBefore = board.Items.Select(item => item.RelativePath).ToArray();

        await ExecuteCommandAsync(board.ReorderCardCommand.Execute(
            new ReorderCardRequest("part-one/chapter-one.md", NewIndex: 1)));

        Assert.Same(card.Card, board.SelectedCard);
        Assert.Equal(itemsBefore, board.Items.Select(item => item.RelativePath));
        Assert.Single(context.NotificationService.Errors);
        Assert.Contains("disk write exploded", context.NotificationService.Errors[0]);
        Assert.Equal("Reorder card", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Equal("disk write exploded", board.LastStructuralError?.Message);
        Assert.Equal(0, context.Workspace.ReloadCount);
    }

    [Fact]
    public void CardDisplaySize_DefaultsToLarge()
    {
        using var context = CreateContext();
        using var board = context.CreateCorkboard();

        Assert.Equal(CardDisplaySize.Large, board.CardDisplaySize);
    }

    [Fact]
    public async Task SetCardSizeCommand_UpdatesCardDisplaySize()
    {
        using var context = CreateContext();
        using var board = context.CreateCorkboard();

        await ExecuteCommandAsync(board.SetCardSizeCommand.Execute(CardDisplaySize.Medium));
        Assert.Equal(CardDisplaySize.Medium, board.CardDisplaySize);

        await ExecuteCommandAsync(board.SetCardSizeCommand.Execute(CardDisplaySize.Tiny));
        Assert.Equal(CardDisplaySize.Tiny, board.CardDisplaySize);

        await ExecuteCommandAsync(board.SetCardSizeCommand.Execute(CardDisplaySize.List));
        Assert.Equal(CardDisplaySize.List, board.CardDisplaySize);

        await ExecuteCommandAsync(board.SetCardSizeCommand.Execute(CardDisplaySize.Large));
        Assert.Equal(CardDisplaySize.Large, board.CardDisplaySize);
    }

    [Fact]
    public async Task TogglePartExpandedCommand_CollapsesAndExpandsChildCards()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var part = Assert.IsType<PartDividerItemViewModel>(board.Items[0]);
        var first = GetChapterCard(board, "part-one/chapter-one.md");
        var second = GetChapterCard(board, "part-one/chapter-two.md");

        Assert.Equal(2, part.ChapterCount);
        Assert.True(part.IsExpanded);
        Assert.True(first.IsVisibleOnBoard);
        Assert.True(second.IsVisibleOnBoard);

        await ExecuteCommandAsync(board.TogglePartExpandedCommand.Execute(part));

        Assert.False(part.IsExpanded);
        Assert.False(first.IsVisibleOnBoard);
        Assert.False(second.IsVisibleOnBoard);

        await ExecuteCommandAsync(board.TogglePartExpandedCommand.Execute(part));

        Assert.True(part.IsExpanded);
        Assert.True(first.IsVisibleOnBoard);
        Assert.True(second.IsVisibleOnBoard);
    }

    [Fact]
    public async Task RebuildItems_PreservesCollapsedPartState()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        var part = Assert.IsType<PartDividerItemViewModel>(board.Items[0]);
        await ExecuteCommandAsync(board.TogglePartExpandedCommand.Execute(part));
        Assert.False(part.IsExpanded);

        AddWorkspaceNode(context, CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        Assert.True(
            SpinWait.SpinUntil(
                () => board.Items.Any(item => item.RelativePath == "part-one/chapter-two.md"),
                TimeSpan.FromSeconds(2)),
            "Corkboard items did not rebuild after workspace node collection change.");

        part = Assert.IsType<PartDividerItemViewModel>(board.Items[0]);
        var second = GetChapterCard(board, "part-one/chapter-two.md");

        Assert.False(part.IsExpanded);
        Assert.False(second.IsVisibleOnBoard);
    }

    [Fact]
    public async Task DeleteChapterCommand_RequiresConfirmation()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();
        var card = GetChapterCard(board, "part-one/chapter-one.md");
        await ExecuteCommandAsync(board.SelectCardCommand.Execute(card));

        await ExecuteCommandAsync(board.DeleteChapterCommand.Execute(
            new DeleteChapterRequest("part-one/chapter-one.md", Confirmed: false)));

        Assert.Empty(context.StructureService.DeleteCalls);
        Assert.Single(context.NotificationService.Errors);
        Assert.Contains("confirmation was not provided", context.NotificationService.Errors[0]);
        Assert.Equal("Delete chapter", board.LastStructuralError?.Operation);
        Assert.Equal("part-one/chapter-one.md", board.LastStructuralError?.Path);
        Assert.Equal(0, context.Workspace.ReloadCount);
    }

    [Fact]
    public void BeginInlineCreate_BetweenChapterCards_InsertsPlaceholderAtRequestedVisualPosition()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-one/part.md");

        board.BeginInlineCreate(board.GetInsertIndexAfterChapter("part-one/chapter-one.md"), part);

        var inlineCreate = Assert.IsType<InlineCreateItemViewModel>(board.Items[2]);
        Assert.Equal(2, inlineCreate.BookTxtInsertIndex);
        Assert.Equal("part-one/part.md", inlineCreate.PartPath);
        Assert.Equal(
            new[]
            {
                CorkboardItemKind.PartDivider,
                CorkboardItemKind.ChapterCard,
                CorkboardItemKind.InlineCreate,
                CorkboardItemKind.ChapterCard
            },
            board.Items.Select(item => item.Kind));
    }

    [Fact]
    public void BeginInlineCreate_BeforeFirstPart_InsertsPlaceholderAtBoardStart()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"));

        using var board = context.CreateCorkboard();

        board.BeginInlineCreate(board.GetBookChapterInsertIndex(), part: null);

        var inlineCreate = Assert.IsType<InlineCreateItemViewModel>(board.Items[0]);
        Assert.Equal(0, inlineCreate.BookTxtInsertIndex);
        Assert.Null(inlineCreate.PartPath);
        Assert.Equal(CorkboardItemKind.PartDivider, board.Items[1].Kind);
    }

    [Fact]
    public void BeginInlineCreate_AfterPartIntoEmptyPart_InsertsImmediatelyAfterDivider()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-one/part.md");

        board.BeginInlineCreate(board.GetInsertIndexAfterPart(part.RelativePath), part);

        var inlineCreate = Assert.IsType<InlineCreateItemViewModel>(board.Items[1]);
        Assert.Equal(part.RelativePath, inlineCreate.PartPath);
        Assert.Equal(CorkboardItemKind.EmptyPartHint, board.Items[2].Kind);
    }

    [Fact]
    public void BeginInlineCreate_AfterLastChapterInPart_DoesNotCrossIntoNextPart()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-one/part.md");

        board.BeginInlineCreate(board.GetInsertIndexAfterPart(part.RelativePath), part);

        var inlineCreate = Assert.IsType<InlineCreateItemViewModel>(board.Items[2]);
        Assert.Equal(part.RelativePath, inlineCreate.PartPath);
        Assert.Equal("part-two/part.md", board.Items[3].RelativePath);
    }

    [Fact]
    public void GetInsertIndexAfterChapter_UsesAllBookTxtEntries()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        Assert.Equal(2, board.GetInsertIndexAfterChapter("part-one/chapter-one.md"));
        Assert.Equal(4, board.GetInsertIndexAfterChapter("part-two/chapter-two.md"));
    }

    [Fact]
    public async Task CommitInlineCreateAsync_BetweenChapterCards_CallsCreateNewChapterAndReloads()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-one/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-one/part.md");
        board.BeginInlineCreate(board.GetInsertIndexAfterChapter("part-one/chapter-one.md"), part);

        await board.CommitInlineCreateAsync("Interlude");

        var create = Assert.Single(context.StructureService.CreateCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-one/interlude.md", "# Interlude\n\n", 2), create);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
        Assert.DoesNotContain(board.Items, item => item.Kind == CorkboardItemKind.InlineCreate);
    }

    [Fact]
    public async Task CommitInlineCreateAsync_NestedPart_PreservesPartFolderPrefix()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/act-one/part.md", "Act One", NodeKind.Part));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-two/act-one/part.md");
        board.BeginInlineCreate(board.GetInsertIndexAfterPart(part.RelativePath), part);

        await board.CommitInlineCreateAsync("Scene 1");

        var create = Assert.Single(context.StructureService.CreateCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-two/act-one/scene-1.md", "# Scene 1\n\n", 2), create);
        Assert.Equal(1, context.Workspace.ReloadCount);
        Assert.Null(board.LastStructuralError);
    }

    [Fact]
    public async Task CommitInlineCreateAsync_DuplicatePathFailure_SurfacesCreateChapterErrorWithProposedPath()
    {
        using var context = CreateContext();
        context.EnableWorkspace();
        context.StructureService.CreateResult = Result<Unit>.Fail("Chapter file 'part-two/act-one/scene-1.md' already exists.");
        SeedWorkspaceNodes(context,
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/act-one/part.md", "Act One", NodeKind.Part));

        using var board = context.CreateCorkboard();
        var part = GetPartDivider(board, "part-two/act-one/part.md");
        board.BeginInlineCreate(board.GetInsertIndexAfterPart(part.RelativePath), part);

        await board.CommitInlineCreateAsync("Scene 1");

        var create = Assert.Single(context.StructureService.CreateCalls);
        Assert.Equal((context.Workspace.BookTxtPath, "part-two/act-one/scene-1.md", "# Scene 1\n\n", 2), create);
        Assert.Equal(0, context.Workspace.ReloadCount);
        Assert.Empty(context.MetadataStore.Writes);
        Assert.Equal("Create chapter", board.LastStructuralError?.Operation);
        Assert.Equal("part-two/act-one/scene-1.md", board.LastStructuralError?.Path);
        Assert.Equal(context.Workspace.BookTxtPath, board.LastStructuralError?.BookTxtPath);
        Assert.Contains("already exists", board.LastStructuralError?.Message ?? string.Empty);
        Assert.Contains("already exists", Assert.Single(context.NotificationService.Errors));
    }

    [Fact]
    public void GetBookChapterInsertIndex_WithParts_ReturnsIndexOfFirstPart()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "prologue.md", "Prologue"),
            CreateChapter(context, "part-one/part.md", "Part One", NodeKind.Part),
            CreateChapter(context, "part-one/chapter-one.md", "Chapter One"),
            CreateChapter(context, "part-two/part.md", "Part Two", NodeKind.Part),
            CreateChapter(context, "part-two/chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        // The first Part is at node index 1 (prologue is 0).
        Assert.Equal(1, board.GetBookChapterInsertIndex());
    }

    [Fact]
    public void GetBookChapterInsertIndex_WithNoParts_ReturnsNodeCount()
    {
        using var context = CreateContext();
        SeedWorkspaceNodes(context,
            CreateChapter(context, "chapter-one.md", "Chapter One"),
            CreateChapter(context, "chapter-two.md", "Chapter Two"));

        using var board = context.CreateCorkboard();

        Assert.Equal(2, board.GetBookChapterInsertIndex());
    }

    [Fact]
    public void GetBookChapterInsertIndex_WithEmptyBook_ReturnsZero()
    {
        using var context = CreateContext();

        using var board = context.CreateCorkboard();

        Assert.Equal(0, board.GetBookChapterInsertIndex());
    }

    private static TestContext CreateContext() => new();

    private static ChapterViewModel CreateChapter(TestContext context, string relativePath, string title, NodeKind kind = NodeKind.Chapter)
    {
        var node = new ChapterNode(relativePath, relativePath, title, kind, false, 0);
        return new ChapterViewModel(
            node,
            Guid.NewGuid().ToString("N"),
            null,
            context.PhaseDataService,
            context.TargetsService,
            context.SettingsStore,
            context.NotificationService,
            context.WorkspaceRoot);
    }

    private static void SeedWorkspaceNodes(TestContext context, params ChapterViewModel[] nodes)
    {
        var collection = GetWorkspaceNodeCollection(context.Workspace);
        collection.Clear();
        foreach (var node in nodes)
            collection.Add(node);
    }

    private static void AddWorkspaceNode(TestContext context, ChapterViewModel node)
    {
        GetWorkspaceNodeCollection(context.Workspace).Add(node);
    }

    private static ChapterCardItemViewModel GetChapterCard(CorkboardViewModel board, string relativePath)
        => Assert.IsType<ChapterCardItemViewModel>(board.Items.First(item => item.RelativePath == relativePath));

    private static PartDividerItemViewModel GetPartDivider(CorkboardViewModel board, string relativePath)
        => Assert.IsType<PartDividerItemViewModel>(board.Items.First(item => item.RelativePath == relativePath && item.Kind == CorkboardItemKind.PartDivider));

    private static IList GetWorkspaceNodeCollection(WorkspaceViewModel workspace)
    {
        var field = typeof(WorkspaceViewModel).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace node collection field was not found.");

        return (IList)(field.GetValue(workspace)
            ?? throw new InvalidOperationException("Workspace node collection was null."));
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

    private static bool WaitForFirstCanExecute(ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> command)
    {
        var observed = false;
        using var subscription = command.CanExecute.Subscribe(value => observed = value);
        return observed;
    }

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public FakeNotificationService NotificationService { get; } = new();
        public FakeMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPickerService { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel EditorViewModel { get; }
        public ChapterRegistryService RegistryService { get; }
        public PhaseDataService PhaseDataService { get; }
        public TargetsService TargetsService { get; }
        public WordCountHistoryService HistoryService { get; }
        public ManuscriptService ManuscriptService { get; }
        public FakeBookTxtStructureService StructureService { get; } = new();
        public FakeOrphanFileDiscoveryService OrphanDiscovery { get; } = new();
        public SpyWorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; } = Path.Combine("C:", "Dev", "Hymnal", "tests", "workspace");
        public string ManuscriptRoot { get; } = Path.Combine("C:", "Dev", "Hymnal", "tests", "workspace", "manuscript");

        public TestContext()
        {
            EditorViewModel = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            RegistryService = new ChapterRegistryService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            HistoryService = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService));
            Workspace = new SpyWorkspaceViewModel(
                ManuscriptService,
                StructureService,
                new FakeFilePickerService(),
                SettingsStore,
                FolderPickerService,
                NotificationService,
                EditorViewModel,
                RegistryService,
                PhaseDataService,
                TargetsService,
                WordCountService,
                HistoryService);
        }

        public CorkboardViewModel CreateCorkboard() => new(
            Workspace,
            StructureService,
            OrphanDiscovery,
            SettingsStore,
            NotificationService,
            ManuscriptService);

        public void EnableWorkspace()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);
            SetPrivateField(Workspace, "_model", model);
            SetPrivateField(Workspace, "_hasWorkspace", true);
        }

        public void Dispose() => _owned.Dispose();
    }

    private sealed class SpyWorkspaceViewModel : WorkspaceViewModel
    {
        public int ReloadCount { get; private set; }
        public int ReorderCount { get; private set; }
        public Result<Unit> ReloadResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> ReorderResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Action? OnReload { get; set; }

        public SpyWorkspaceViewModel(
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
            WordCountHistoryService historyService)
            : base(manuscriptService, structureService, filePicker, settingsStore, folderPicker, notificationService, editor, registryService, phaseDataService, targetsService, wordCountService, historyService)
        {
        }

        public override Task<Result<Unit>> ReloadCurrentWorkspaceAsync()
        {
            ReloadCount++;
            if (ReloadResult.IsSuccess)
                OnReload?.Invoke();

            return Task.FromResult(ReloadResult);
        }

        public override Task<Result<Unit>> ReorderNodesAsync()
        {
            ReorderCount++;
            return Task.FromResult(ReorderResult);
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public List<string> Successes { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) => Successes.Add(message);
    }

    private sealed class FakeMetadataStore : IMetadataStore
    {
        public List<(string Path, string Content)> Writes { get; } = new();

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Writes.Add((absolutePath, content));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
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

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeOrphanFileDiscoveryService : IOrphanFileDiscoveryService
    {
        public IReadOnlyList<OrphanFileInfo> Orphans { get; set; } = Array.Empty<OrphanFileInfo>();

        public Task<IReadOnlyList<OrphanFileInfo>> DiscoverAsync(
            string manuscriptRoot,
            IReadOnlyList<string> bookTxtEntries)
            => Task.FromResult(Orphans);
    }

    private sealed class FakeBookTxtStructureService : IBookTxtStructureService
    {
        public List<(string BookTxtPath, string ChapterPath, int NewIndex)> ReorderCalls { get; } = new();
        public List<(string BookTxtPath, string ExistingPath, string ReplacementPath, int NewIndex)> MoveCalls { get; } = new();
        public List<(string BookTxtPath, string ExistingPath, string ReplacementPath)> RenameCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath, string Content, int Index)> CreateCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath, int Index)> AddExistingIndexCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath, string PartPath)> AddExistingPartCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath, int Index)> IncludeExistingIndexCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath, string PartPath)> IncludeExistingPartCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath)> RemoveCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath)> ExcludeCalls { get; } = new();
        public List<(string BookTxtPath, string ChapterPath)> DeleteCalls { get; } = new();

        public Result<Unit> ReorderResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> MoveResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> RenameResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> CreateResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> AddExistingIndexResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> AddExistingPartResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> IncludeExistingIndexResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> IncludeExistingPartResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> RemoveResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> ExcludeResult { get; set; } = Result<Unit>.Ok(Unit.Default);
        public Result<Unit> DeleteResult { get; set; } = Result<Unit>.Ok(Unit.Default);

        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath)
            => Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
        {
            ReorderCalls.Add((bookTxtPath, chapterPath, newIndex));
            return Task.FromResult(ReorderResult);
        }

        public Task<Result<Unit>> MoveEntryAsync(string bookTxtPath, string existingPath, string replacementPath, int newIndex)
        {
            MoveCalls.Add((bookTxtPath, existingPath, replacementPath, newIndex));
            return Task.FromResult(MoveResult);
        }

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
        {
            RenameCalls.Add((bookTxtPath, existingPath, replacementPath));
            return Task.FromResult(RenameResult);
        }

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
        {
            AddExistingIndexCalls.Add((bookTxtPath, chapterPath, index));
            return Task.FromResult(AddExistingIndexResult);
        }

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
        {
            AddExistingPartCalls.Add((bookTxtPath, chapterPath, partPath));
            return Task.FromResult(AddExistingPartResult);
        }

        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
        {
            IncludeExistingIndexCalls.Add((bookTxtPath, chapterPath, index));
            return Task.FromResult(IncludeExistingIndexResult);
        }

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
        {
            IncludeExistingPartCalls.Add((bookTxtPath, chapterPath, partPath));
            return Task.FromResult(IncludeExistingPartResult);
        }

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
        {
            CreateCalls.Add((bookTxtPath, chapterPath, content, index));
            return Task.FromResult(CreateResult);
        }

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
            => Task.FromResult(CreateResult);

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
        {
            RemoveCalls.Add((bookTxtPath, chapterPath));
            return Task.FromResult(RemoveResult);
        }

        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath)
        {
            ExcludeCalls.Add((bookTxtPath, chapterPath));
            return Task.FromResult(ExcludeResult);
        }

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
        {
            DeleteCalls.Add((bookTxtPath, chapterPath));
            return Task.FromResult(DeleteResult);
        }
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var type = target.GetType();
        FieldInfo? field = null;

        while (type != null && field == null)
        {
            field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            type = type.BaseType;
        }

        if (field == null)
            throw new InvalidOperationException($"Field '{fieldName}' was not found on {target.GetType().Name}.");

        field.SetValue(target, value);
    }
}
