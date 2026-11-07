using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class CorkboardViewSmokeTests
{
    static CorkboardViewSmokeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public void CorkboardView_LoadsXaml_AndTogglesEmptyStateWithDataContext()
    {
        ReactiveUiTestBootstrap.RunOnUiThread(() =>
        {
            using var context = new TestContext();
            using var emptyBoard = context.CreateBoard();

            var emptyView = new CorkboardView
            {
                DataContext = emptyBoard
            };

            LayoutView(emptyView);

            Assert.False(emptyBoard.HasItems);
            Assert.NotNull(emptyView.FindControl<TextBlock>("EmptyBoardState"));
            Assert.True(emptyView.FindControl<TextBlock>("EmptyBoardState")!.IsVisible);
            Assert.False(emptyView.FindControl<ScrollViewer>("BoardScroll")!.IsVisible);
            Assert.Same(emptyBoard.Items, emptyView.FindControl<ItemsControl>("BoardItems")!.ItemsSource);

            var chapter = context.AddNode("part-one/chapter-one.md", "Chapter One");
            context.SeedWorkspace(chapter);

            using var populatedBoard = context.CreateBoard();
            var populatedView = new CorkboardView
            {
                DataContext = populatedBoard
            };

            LayoutView(populatedView);

            Assert.True(populatedBoard.HasItems);
            Assert.False(populatedView.FindControl<TextBlock>("EmptyBoardState")!.IsVisible);
            Assert.True(populatedView.FindControl<ScrollViewer>("BoardScroll")!.IsVisible);
            Assert.Same(populatedBoard.Items, populatedView.FindControl<ItemsControl>("BoardItems")!.ItemsSource);
            Assert.Same(populatedBoard.Items, populatedView.FindControl<ItemsControl>("BoardItemsList")!.ItemsSource);
            Assert.Equal(CardDisplaySize.Large, populatedBoard.CardDisplaySize);
            Assert.True(populatedView.FindControl<ItemsControl>("BoardItems")!.IsVisible);
            Assert.False(populatedView.FindControl<ItemsControl>("BoardItemsList")!.IsVisible);

            populatedBoard.SetCardSizeCommand.Execute(CardDisplaySize.List).Subscribe();
            LayoutView(populatedView);

            Assert.False(populatedView.FindControl<ItemsControl>("BoardItems")!.IsVisible);
            Assert.True(populatedView.FindControl<ItemsControl>("BoardItemsList")!.IsVisible);
        });
    }

    [Fact]
    public void CorkboardView_XamlDeclaresStructuralMenusExcludedAffordancesAndDropTargets()
    {
        var xaml = File.ReadAllText(GetCorkboardViewPath());

        Assert.Contains("Loaded=\"Card_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"PartHeader_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"EmptyPartHint_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StringFormat='New {0}…'", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"NewChapter_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Remove from Book\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Include in Book\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"EXCLUDED\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Excluded from Book.txt. Right-click to include it again.", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CorkboardView_DragHelpers_AllowOnlyIncludedPresentChapterCardsAsSources()
    {
        using var context = new TestContext();
        var source = new ChapterCardItemViewModel(new CardViewModel(context.AddNode("chapter-one.md", "Chapter One")), owningPart: null);
        var missing = new ChapterCardItemViewModel(new CardViewModel(context.AddNode("missing.md", "Missing", isMissing: true)), owningPart: null);
        var part = new PartDividerItemViewModel(context.AddNode("part-one/part.md", "Part One", NodeKind.Part), chapterCount: 0, isFirstPart: true, isExpanded: true);
        var emptyPart = new EmptyPartHintItemViewModel(part.Part, part);
        var inlineCreate = new InlineCreateItemViewModel(0, part.RelativePath, part);
        var excluded = new ExcludedChapterCardItemViewModel(new OrphanFileInfo("excluded.md", "Excluded", null), owningPart: null);

        Assert.True(CorkboardView.CanDragFromCorkboard(source));
        Assert.False(CorkboardView.CanDragFromCorkboard(missing));
        Assert.False(CorkboardView.CanDragFromCorkboard(part));
        Assert.False(CorkboardView.CanDragFromCorkboard(emptyPart));
        Assert.False(CorkboardView.CanDragFromCorkboard(inlineCreate));
        Assert.False(CorkboardView.CanDragFromCorkboard(excluded));

        source.Dispose();
        missing.Dispose();
        part.Dispose();
        emptyPart.Dispose();
        inlineCreate.Dispose();
        excluded.Dispose();
    }

    [Fact]
    public void CorkboardView_DragHelpers_CreateRichRequestsForCardsPartsAndEmptyParts()
    {
        using var context = new TestContext();
        var partOne = new PartDividerItemViewModel(context.AddNode("part-one/part.md", "Part One", NodeKind.Part), chapterCount: 1, isFirstPart: true, isExpanded: true);
        var partTwo = new PartDividerItemViewModel(context.AddNode("part-two/part.md", "Part Two", NodeKind.Part), chapterCount: 0, isFirstPart: false, isExpanded: true);
        var source = new ChapterCardItemViewModel(new CardViewModel(context.AddNode("part-one/chapter-one.md", "Chapter One")), partOne);
        var sibling = new ChapterCardItemViewModel(new CardViewModel(context.AddNode("part-one/chapter-two.md", "Chapter Two")), partOne);
        var emptyPart = new EmptyPartHintItemViewModel(partTwo.Part, partTwo);

        Assert.True(CorkboardView.CanDropOnCorkboardCard(source, sibling));
        Assert.True(CorkboardView.CanDropIntoCorkboardPart(source, partTwo));
        Assert.True(CorkboardView.CanDropIntoCorkboardEmptyPart(source, emptyPart));

        var afterSibling = Assert.IsType<CorkboardDropRequest>(CorkboardView.CreateCardDropRequest(source, sibling, dropBefore: false));
        Assert.Equal("part-one/chapter-one.md", afterSibling.RelativePath);
        Assert.Equal("part-one/part.md", afterSibling.TargetPartPath);
        Assert.Equal("part-one/chapter-two.md", afterSibling.AfterRelativePath);
        Assert.Null(afterSibling.BeforeRelativePath);

        var beforeSibling = Assert.IsType<CorkboardDropRequest>(CorkboardView.CreateCardDropRequest(source, sibling, dropBefore: true));
        Assert.Equal("part-one/chapter-two.md", beforeSibling.BeforeRelativePath);
        Assert.Null(beforeSibling.AfterRelativePath);

        var intoPart = Assert.IsType<CorkboardDropRequest>(CorkboardView.CreatePartDropRequest(source, partTwo));
        Assert.Equal("part-two/part.md", intoPart.TargetPartPath);
        Assert.Null(intoPart.AfterRelativePath);
        Assert.Null(intoPart.BeforeRelativePath);

        var intoEmptyPart = Assert.IsType<CorkboardDropRequest>(CorkboardView.CreateEmptyPartDropRequest(source, emptyPart));
        Assert.Equal("part-two/part.md", intoEmptyPart.TargetPartPath);
        Assert.Null(intoEmptyPart.AfterRelativePath);
        Assert.Null(intoEmptyPart.BeforeRelativePath);

        source.Dispose();
        sibling.Dispose();
        emptyPart.Dispose();
        partOne.Dispose();
        partTwo.Dispose();
    }

    [Fact]
    public void CorkboardView_IncludeExcludedCardRouting_UsesPartPathOrCanonicalBookIndex()
    {
        using var context = new TestContext();
        var opener = context.AddNode("front-matter.md", "Front Matter");
        var partChapter = context.AddNode("part-one/chapter-one.md", "Chapter One");
        var partNode = context.AddNode("part-one/part.md", "Part One", NodeKind.Part);
        context.SeedWorkspace(opener, partNode, partChapter);

        using var board = context.CreateBoard();
        var part = Assert.Single(board.Items.OfType<PartDividerItemViewModel>());

        using var partExcluded = new ExcludedChapterCardItemViewModel(
            new OrphanFileInfo("part-one/excluded.md", "Excluded in Part", null),
            part);
        var partRequest = CorkboardView.CreateIncludeExcludedCardRequest(board, partExcluded);
        Assert.Equal("part-one/excluded.md", partRequest.ChapterPath);
        Assert.Equal("part-one/part.md", partRequest.PartPath);
        Assert.Null(partRequest.Index);

        using var bookExcluded = new ExcludedChapterCardItemViewModel(
            new OrphanFileInfo("book-level-excluded.md", "Book Level Excluded", null),
            owningPart: null);
        var bookRequest = CorkboardView.CreateIncludeExcludedCardRequest(board, bookExcluded);
        Assert.Equal("book-level-excluded.md", bookRequest.ChapterPath);
        Assert.Equal(board.GetBookChapterInsertIndex(), bookRequest.Index);
        Assert.Null(bookRequest.PartPath);
    }

    [Fact]
    public void CorkboardView_NewChapterRouting_UsesCanonicalPlacementHelpers()
    {
        using var context = new TestContext();
        var opener = context.AddNode("front-matter.md", "Front Matter");
        var partNode = context.AddNode("part-one/part.md", "Part One", NodeKind.Part);
        var chapterOne = context.AddNode("part-one/chapter-one.md", "Chapter One");
        var chapterTwo = context.AddNode("part-one/chapter-two.md", "Chapter Two");
        context.SeedWorkspace(opener, partNode, chapterOne, chapterTwo);

        using var board = context.CreateBoard();
        var part = Assert.Single(board.Items.OfType<PartDividerItemViewModel>());
        var chapterCard = board.Items.OfType<ChapterCardItemViewModel>()
            .First(card => string.Equals(card.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));

        var afterChapter = CorkboardView.CreateInlineCreatePlacementAfterChapter(board, chapterCard);
        Assert.Equal(board.GetInsertIndexAfterChapter("part-one/chapter-one.md"), afterChapter.InsertIndex);
        Assert.Same(part, afterChapter.Part);

        var afterPart = CorkboardView.CreateInlineCreatePlacementAfterPart(board, part);
        Assert.Equal(board.GetInsertIndexAfterPart("part-one/part.md"), afterPart.InsertIndex);
        Assert.Same(part, afterPart.Part);

        var atBookLevel = CorkboardView.CreateInlineCreatePlacementAtBookLevel(board);
        Assert.Equal(board.GetBookChapterInsertIndex(), atBookLevel.InsertIndex);
        Assert.Null(atBookLevel.Part);
    }

    [Fact]
    public void CorkboardView_IncludeExistingChapterAfterCard_UsesCanonicalInsertIndex()
    {
        using var context = new TestContext();
        var opener = context.AddNode("front-matter.md", "Front Matter");
        var partNode = context.AddNode("part-one/part.md", "Part One", NodeKind.Part);
        var chapterOne = context.AddNode("part-one/chapter-one.md", "Chapter One");
        context.SeedWorkspace(opener, partNode, chapterOne);

        using var board = context.CreateBoard();
        var chapterCard = board.Items.OfType<ChapterCardItemViewModel>()
            .First(card => string.Equals(card.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));

        var request = CorkboardView.CreateIncludeExistingChapterRequestAfterCard(
            board,
            chapterCard,
            "part-one/existing.md");

        Assert.Equal("part-one/existing.md", request.ChapterPath);
        Assert.Equal(board.GetInsertIndexAfterChapter("part-one/chapter-one.md"), request.Index);
        Assert.Null(request.PartPath);
    }

    // Manual smoke checklist for real desktop drag/drop coverage that is brittle to automate:
    // 1. Open a sample workspace and click PLAN.
    // 2. Verify Part dividers, empty-Part hints, and chapter cards render in the corkboard.
    // 3. Drag an included present chapter card within the same Part and drop on another card; verify before/after placement updates after reload.
    // 4. Drag a chapter card across Parts and drop on another chapter card in the destination Part; verify Book.txt order changes and the chapter file moves folders.
    // 5. Drag a chapter card onto an empty Part hint; verify the drop succeeds without requiring a child card.
    // 6. Collapse a Part, then drag a chapter card onto that Part header; verify the drop still succeeds while child cards are hidden.
    // 7. Quit and restart the app, reopen the workspace, and verify the moved chapter remains in the new Part/path.
    // 8. Attempt invalid drags from missing/excluded cards or onto invalid targets and confirm the drop is ignored or the visible structural error/notification surfaces when a rejected request reaches the ViewModel.

    private static void LayoutView(Control view)
    {
        view.ApplyTemplate();
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
    }

    private static string GetCorkboardViewPath() => Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Hymnal",
            "Views",
            "CorkboardView.axaml"));

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
        public SpyWorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }

        public TestContext()
        {
            var root = Path.Combine(Path.GetTempPath(), "hymnal-corkboard-view-smoke-tests", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = root;
            ManuscriptRoot = Path.Combine(root, "manuscript");
            Directory.CreateDirectory(ManuscriptRoot);

            EditorViewModel = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService));
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

            SeedWorkspace(Array.Empty<ChapterViewModel>());
            SetWorkspaceModel(false);
        }

        public ChapterViewModel AddNode(
            string relativePath,
            string title,
            NodeKind kind = NodeKind.Chapter,
            bool isMissing = false)
        {
            if (!isMissing)
            {
                var absolutePath = Path.Combine(ManuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                File.WriteAllText(absolutePath, kind == NodeKind.Part
                    ? $"{title}\n{{class: part}}\n"
                    : $"# {title}\n\nBody text.");
            }

            var node = new ChapterNode(relativePath, relativePath, title, kind, IsMissing: isMissing, Index: 0);
            return new ChapterViewModel(
                node,
                uuid: Guid.NewGuid().ToString("N"),
                phaseData: null,
                PhaseDataService,
                TargetsService,
                SettingsStore,
                NotificationService,
                WorkspaceRoot);
        }

        public void SeedWorkspace(params ChapterViewModel[] nodes)
        {
            var collection = GetWorkspaceNodeCollection(Workspace);
            collection.Clear();
            foreach (var node in nodes)
                collection.Add(node);

            SetWorkspaceModel(nodes.Length > 0);
        }

        public CorkboardViewModel CreateBoard() => new(
            Workspace,
            StructureService,
            new OrphanFileDiscoveryService(),
            SettingsStore,
            NotificationService,
            ManuscriptService);

        private void SetWorkspaceModel(bool hasWorkspace)
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);

            SetPrivateField(Workspace, "_model", model);
            SetPrivateField(Workspace, "_hasWorkspace", hasWorkspace);
            SetPrivateField(Workspace, "_workspaceName", Path.GetFileName(WorkspaceRoot));
        }

        public void Dispose()
        {
            try
            {
                _owned.Dispose();
            }
            finally
            {
                DeleteTempFolders();
            }
        }

        private void DeleteTempFolders()
        {
            try
            {
                if (Directory.Exists(WorkspaceRoot))
                    Directory.Delete(WorkspaceRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private sealed class SpyWorkspaceViewModel : WorkspaceViewModel
    {
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
        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            return File.WriteAllTextAsync(absolutePath, content);
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
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class FakeBookTxtStructureService : IBookTxtStructureService
    {
        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath)
            => Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> MoveEntryAsync(string bookTxtPath, string existingPath, string replacementPath, int newIndex)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));
    }

    private static IList GetWorkspaceNodeCollection(WorkspaceViewModel workspace)
    {
        var field = typeof(WorkspaceViewModel).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace node collection field was not found.");

        return (IList)(field.GetValue(workspace)
            ?? throw new InvalidOperationException("Workspace node collection was null."));
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on WorkspaceViewModel.");

        field.SetValue(target, value);
    }
}
