using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

/// <summary>
/// Plan mode for story and anthology projects. A Short Story shows its one beat board (with a Files
/// strip of the file Corkboard when it has several scene files). An anthology shows the Corkboard of
/// its stories and drills into a story's beat board, with a breadcrumb back. Novels (and no workspace)
/// show the Corkboard. The board is closed whenever the project stops showing it.
/// </summary>
public sealed class PlanHostViewModel : ViewModelBase, IDisposable
{
    public const string CollectionBreadcrumb = "Collection";
    public const string DefaultStoryTitle = "Story";

    /// <summary>A board to show: its workspace, key, title and the scenes its beats may link to.</summary>
    private sealed record BoardRequest(string Root, string Key, string Title, IReadOnlyList<string> Scope)
    {
        /// <summary>True when <paramref name="other"/> is the same board (same workspace and key).</summary>
        public bool IsSameBoard(BoardRequest? other) => other != null && other.Root == Root && other.Key == Key;

        public bool Matches(BoardRequest? other) =>
            IsSameBoard(other) && other!.Title == Title && other.Scope.SequenceEqual(Scope);
    }

    /// <summary>A scene board to move to a Part's key before the Part's board opens.</summary>
    private sealed record Adoption(string FromKey, string SceneTitle, string PartTitle);

    private readonly WorkspaceViewModel _workspace;
    private readonly INotificationService? _notifications;
    private readonly IBeatBoardService? _boards;

    private ProjectKind? _shownKind;
    private string? _shownRoot;
    /// <summary>Anthology: registry UUID of the story whose board is shown; null on the collection.</summary>
    private string? _drilledStoryUuid;
    /// <summary>The board last asked of <see cref="BeatBoard"/>; null when it was asked to close.</summary>
    private BoardRequest? _requested;
    private int _pendingBoardOps;
    private bool _refreshQueued;
    private bool _disposed;

    public PlanHostViewModel(
        WorkspaceViewModel workspace,
        CorkboardViewModel corkboard,
        BeatBoardViewModel beatBoard,
        INotificationService? notifications = null,
        IBeatBoardService? boards = null)
    {
        _workspace = workspace;
        _notifications = notifications;
        _boards = boards;
        Corkboard = corkboard;
        BeatBoard = beatBoard;
        _content = corkboard;

        BackToCollectionCommand = ReactiveCommand.Create(BackToCollection, this.WhenAnyValue(x => x.ShowBreadcrumb));
        Disposables.Add(BackToCollectionCommand.ThrownExceptions.Subscribe(ex => _notifications?.ShowError(ex.Message)));

        Disposables.Add(corkboard.BeatsRequested.Subscribe(OpenStory));

        // Profile, workspace and book changes are coalesced into one refresh after the change settles:
        // a reload clears the node list before refilling it, and the board must not follow the gap.
        Disposables.Add(
            Observable.Merge(
                    workspace.WhenAnyValue(x => x.Profile).Select(_ => Unit.Default),
                    workspace.WhenAnyValue(x => x.HasWorkspace).Select(_ => Unit.Default),
                    workspace.WorkspaceChanged.Select(_ => Unit.Default),
                    workspace.Nodes.ToObservableChangeSet().AutoRefresh(n => n.Node).Select(_ => Unit.Default))
                .Subscribe(_ => QueueRefresh()));
    }

    public CorkboardViewModel Corkboard { get; }
    public BeatBoardViewModel BeatBoard { get; }

    private object _content;
    /// <summary>What Plan mode shows: <see cref="BeatBoard"/> or <see cref="Corkboard"/>.</summary>
    public object Content
    {
        get => _content;
        private set => this.RaiseAndSetIfChanged(ref _content, value);
    }

    private bool _showBreadcrumb;
    /// <summary>True while an anthology story's board is shown.</summary>
    public bool ShowBreadcrumb
    {
        get => _showBreadcrumb;
        private set => this.RaiseAndSetIfChanged(ref _showBreadcrumb, value);
    }

    public string BreadcrumbParent => CollectionBreadcrumb;

    private bool _showFilesStrip;
    /// <summary>True for a Short Story with two or more scene files.</summary>
    public bool ShowFilesStrip
    {
        get => _showFilesStrip;
        private set
        {
            if (_showFilesStrip == value)
                return;
            this.RaiseAndSetIfChanged(ref _showFilesStrip, value);
            this.RaisePropertyChanged(nameof(FilesStripContent));
        }
    }

    /// <summary>The Files strip's content: the Corkboard while the strip is shown, otherwise null.</summary>
    public object? FilesStripContent => ShowFilesStrip ? Corkboard : null;

    private bool _isFilesStripExpanded;
    public bool IsFilesStripExpanded
    {
        get => _isFilesStripExpanded;
        set => this.RaiseAndSetIfChanged(ref _isFilesStripExpanded, value);
    }

    public ReactiveCommand<Unit, Unit> BackToCollectionCommand { get; }

    /// <summary>Writes the shown board's pending edits; true when nothing is left unsaved.</summary>
    public Task<bool> FlushAsync() => BeatBoard.FlushAsync();

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OpenStory(ChapterViewModel story)
    {
        if (_shownKind != ProjectKind.Anthology || !BeatBoardService.IsValidBoardKey(story.Uuid))
            return;
        _drilledStoryUuid = story.Uuid;
        Refresh();
    }

    /// <summary>Leaves the story's board (its pending edits are written to it) for the collection.</summary>
    private void BackToCollection()
    {
        _drilledStoryUuid = null;
        Refresh();
    }

    // ── Following the workspace ───────────────────────────────────────────────

    private void QueueRefresh()
    {
        if (_refreshQueued || _disposed)
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        if (_disposed)
            return;

        var profile = _workspace.Profile;
        ProjectKind? kind = _workspace.HasWorkspace && profile.UsesBeatBoard ? profile.Kind : null;
        var root = _workspace.HasWorkspace ? _workspace.WorkspaceRoot : null;
        if (kind != _shownKind || root != _shownRoot)
            _drilledStoryUuid = null;
        _shownKind = kind;
        _shownRoot = root;

        switch (kind)
        {
            case ProjectKind.Story:
                ShowStory();
                break;
            case ProjectKind.Anthology:
                ShowAnthology();
                break;
            default:
                ShowCorkboard();
                break;
        }
    }

    private void ShowStory()
    {
        var chapters = IncludedChapters(_workspace.Nodes).ToList();
        var title = chapters.FirstOrDefault()?.Node.Title is { Length: > 0 } first ? first : DefaultStoryTitle;
        RequestBoard(new BoardRequest(_shownRoot ?? "", BeatBoardService.ShortStoryBoardKey, title, Scope(chapters)));
        Content = BeatBoard;
        ShowBreadcrumb = false;
        ShowFilesStrip = chapters.Count >= 2;
    }

    private void ShowAnthology()
    {
        ShowFilesStrip = false;
        var story = _drilledStoryUuid == null
            ? null
            : _workspace.Nodes.FirstOrDefault(n => n.Uuid == _drilledStoryUuid && !n.Node.IsExcluded);
        if (story == null)
        {
            ShowCorkboard();
            return;
        }

        var scenes = StoryScenes(story).ToList();
        var request = new BoardRequest(_shownRoot ?? "", story.Uuid, story.Node.Title, Scope(scenes));
        RequestBoard(request, story.Node.Kind == NodeKind.Part ? FindAdoption(request, scenes) : null);
        Content = BeatBoard;
        ShowBreadcrumb = true;
    }

    private void ShowCorkboard()
    {
        _drilledStoryUuid = null;
        Content = Corkboard;
        ShowBreadcrumb = false;
        ShowFilesStrip = false;
        if (_requested != null)
        {
            _requested = null;
            _ = TrackAsync(BeatBoard.CloseBoardAsync());
        }
    }

    /// <summary>The scene files of an anthology story: itself, or the chapters inside its Part.</summary>
    private IEnumerable<ChapterViewModel> StoryScenes(ChapterViewModel story)
    {
        if (story.Node.Kind == NodeKind.Chapter)
            return IncludedChapters([story]);

        var nodes = _workspace.Nodes.ToList();
        var partIndex = nodes.IndexOf(story);
        return IncludedChapters(nodes.Skip(partIndex + 1).TakeWhile(n => n.Node.Kind != NodeKind.Part));
    }

    /// <summary>Chapters a beat may link to: in Book.txt and present on disk.</summary>
    private static IEnumerable<ChapterViewModel> IncludedChapters(IEnumerable<ChapterViewModel> nodes) =>
        nodes.Where(n => n.Node.Kind == NodeKind.Chapter && !n.Node.IsExcluded && !n.Node.IsMissing);

    private static IReadOnlyList<string> Scope(IEnumerable<ChapterViewModel> chapters) =>
        chapters.Select(c => c.Node.RelativePath).ToList();

    /// <summary>
    /// A story that became a Part (its file now the Part's first scene) keeps its beats: when the Part has
    /// no board yet and its first scene has one, that board is moved to the Part. Other scenes keep theirs.
    /// </summary>
    private Adoption? FindAdoption(BoardRequest request, IReadOnlyList<ChapterViewModel> scenes)
    {
        if (_boards == null || scenes.Count == 0 || request.IsSameBoard(_requested))
            return null;
        var first = scenes[0];
        if (!BeatBoardService.IsValidBoardKey(first.Uuid) || first.Uuid == request.Key
            || _boards.BoardExists(request.Root, request.Key) || !_boards.BoardExists(request.Root, first.Uuid))
            return null;
        return new Adoption(first.Uuid, first.Node.Title, request.Title);
    }

    private void RequestBoard(BoardRequest request, Adoption? adoption = null)
    {
        if (request.Matches(_requested))
            return;

        // Same board, new title or scenes (a rename or reorder): update it in place rather than reopen.
        if (request.IsSameBoard(_requested) && _pendingBoardOps == 0 && BeatBoard.BoardKey == request.Key)
        {
            _requested = request;
            BeatBoard.UpdateScope(request.Title, request.Scope);
            return;
        }

        _requested = request;
        _ = TrackAsync(adoption == null
            ? BeatBoard.OpenBoardAsync(request.Key, request.Title, request.Scope)
            : AdoptThenOpenAsync(request, adoption));
    }

    private async Task AdoptThenOpenAsync(BoardRequest request, Adoption adoption)
    {
        // The scene's board may still be shown; close it first so a late save can't recreate its file.
        if (BeatBoard.BoardKey == adoption.FromKey)
            await BeatBoard.CloseBoardAsync();
        if (_disposed || !ReferenceEquals(_requested, request))
            return;

        var moved = await _boards!.MoveBoardAsync(request.Root, adoption.FromKey, request.Key);
        if (moved.IsSuccess)
            _notifications?.ShowInfo($"Moved beats from '{adoption.SceneTitle}' to the story '{adoption.PartTitle}'.");
        else
            _notifications?.ShowError($"Could not move the beats from '{adoption.SceneTitle}': {moved.Error}");

        // Another board was asked for while moving; opening this one now would replace it.
        if (_disposed || !ReferenceEquals(_requested, request))
            return;
        await BeatBoard.OpenBoardAsync(request.Key, request.Title, request.Scope);
    }

    /// <summary>
    /// Waits for a board open or close. The board also closes itself when its workspace closes, which
    /// can cancel an open asked for here; once nothing is in flight, a board that isn't the one asked
    /// for is asked for again.
    /// </summary>
    private async Task TrackAsync(Task boardOperation)
    {
        _pendingBoardOps++;
        try
        {
            await boardOperation;
        }
        catch (Exception ex)
        {
            _notifications?.ShowError($"Failed to open the beat board: {ex.Message}");
        }
        finally
        {
            _pendingBoardOps--;
        }

        if (_disposed || _pendingBoardOps > 0 || _requested == null || BeatBoard.BoardKey == _requested.Key)
            return;
        _requested = null;
        QueueRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disposables.Dispose();
    }
}
