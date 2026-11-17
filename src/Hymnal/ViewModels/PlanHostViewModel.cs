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
    public const string DefaultStoryTitle = StoryStructure.DefaultStoryTitle;

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
        IBeatBoardService? boards = null,
        CollectionPlannerViewModel? planner = null,
        CollectionDetailsViewModel? details = null)
    {
        Planner = planner;
        Details = details;
        _workspace = workspace;
        _notifications = notifications;
        _boards = boards;
        Corkboard = corkboard;
        BeatBoard = beatBoard;
        _content = corkboard;

        BackToCollectionCommand = ReactiveCommand.Create(BackToCollection, this.WhenAnyValue(x => x.ShowBreadcrumb));
        Disposables.Add(BackToCollectionCommand.ThrownExceptions.Subscribe(ex => _notifications?.ShowError(ex.Message)));

        Disposables.Add(corkboard.BeatsRequested.Subscribe(OpenStory));
        if (planner != null)
            Disposables.Add(planner.BeatsRequested.Subscribe(ShowStoryBoard));
        Disposables.Add(workspace.WhenAnyValue(x => x.CollectionDisplayName)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(BreadcrumbParent))));

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

    /// <summary>The Details tab: the open collection at a glance; null when the app runs without it.</summary>
    public CollectionDetailsViewModel? Details { get; }

    private bool _isDetailsView;
    /// <summary>Show the Details tab rather than the project's board, planner or cards (on the collection level).</summary>
    public bool IsDetailsView
    {
        get => _isDetailsView;
        set
        {
            if (value && Details == null)
                return;
            this.RaiseAndSetIfChanged(ref _isDetailsView, value);
            this.RaisePropertyChanged(nameof(IsBoardTabSelected));
            this.RaisePropertyChanged(nameof(IsCardsTabSelected));
            Refresh();
        }
    }

    /// <summary>Opens Plan's Details tab on the collection, leaving any story board that was shown.</summary>
    public void ShowDetails()
    {
        _drilledStoryUuid = null;
        if (Details == null)
            return;
        if (_isDetailsView)
            Refresh();
        else
            IsDetailsView = true;
    }

    /// <summary>Shows the project's own board: the beats, the planner, or the cards.</summary>
    public void ShowBoardTab()
    {
        _isCardsView = false;
        this.RaisePropertyChanged(nameof(IsCardsView));
        IsDetailsView = false;
    }

    /// <summary>Anthology: shows the open collection's cards.</summary>
    public void ShowCardsTab()
    {
        _isCardsView = true;
        this.RaisePropertyChanged(nameof(IsCardsView));
        IsDetailsView = false;
    }

    /// <summary>The second tab's name: Beats (short story), Planner (anthology) or Cards (novel).</summary>
    public string BoardTabLabel => _shownKind switch
    {
        ProjectKind.Story => "Beats",
        ProjectKind.Anthology when Planner != null => "Planner",
        _ => "Cards",
    };

    /// <summary>True when the anthology's planner and cards are separate tabs.</summary>
    public bool ShowsCardsTab => _shownKind == ProjectKind.Anthology && Planner != null;

    public bool IsBoardTabSelected => !IsDetailsView && !(ShowsCardsTab && IsCardsView);
    public bool IsCardsTabSelected => !IsDetailsView && ShowsCardsTab && IsCardsView;

    /// <summary>An anthology's story planner (every collection side by side); null when the app runs without it.</summary>
    public CollectionPlannerViewModel? Planner { get; }

    private bool _isCardsView;
    /// <summary>Anthology: show the open collection's cards (the Corkboard) instead of the planner.</summary>
    public bool IsCardsView
    {
        get => _isCardsView;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCardsView, value);
            this.RaisePropertyChanged(nameof(IsBoardTabSelected));
            this.RaisePropertyChanged(nameof(IsCardsTabSelected));
            Refresh();
        }
    }

    private bool _showsViewSwitch;
    /// <summary>
    /// True on the collection level (not inside an anthology story's board) when there is more than one tab:
    /// Details, and the beats, planner and cards or Corkboard.
    /// </summary>
    public bool ShowsViewSwitch
    {
        get => _showsViewSwitch;
        private set => this.RaiseAndSetIfChanged(ref _showsViewSwitch, value);
    }
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

    /// <summary>The breadcrumb back to the collection: its title, or "Collection".</summary>
    public string BreadcrumbParent => _workspace.CollectionDisplayName;

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

    /// <summary>
    /// Anthology: shows the board of the story with registry id <paramref name="storyUuid"/> (Write mode's
    /// Beats panel asks for this to plan the story being written). Ignored for other project kinds.
    /// </summary>
    public void ShowStoryBoard(string storyUuid)
    {
        // The host may not have caught up with a just-opened workspace yet; settle what it shows first.
        if (_shownKind != ProjectKind.Anthology || _shownRoot != (_workspace.HasWorkspace ? _workspace.WorkspaceRoot : null))
            Refresh();
        if (_shownKind != ProjectKind.Anthology || !BeatBoardService.IsValidBoardKey(storyUuid))
            return;
        _drilledStoryUuid = storyUuid;
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
        this.RaisePropertyChanged(nameof(BoardTabLabel));
        this.RaisePropertyChanged(nameof(ShowsCardsTab));
        this.RaisePropertyChanged(nameof(IsBoardTabSelected));
        this.RaisePropertyChanged(nameof(IsCardsTabSelected));

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
        var story = StoryStructure.ShortStory(_workspace.Nodes);
        RequestBoard(new BoardRequest(_shownRoot ?? "", story.Key, story.Title, story.Scope));
        ShowBreadcrumb = false;
        ShowsViewSwitch = Details != null;
        if (ShowDetailsContent())
        {
            ShowFilesStrip = false;
            return;
        }
        Content = BeatBoard;
        ShowFilesStrip = story.Scenes.Count >= 2;
    }

    /// <summary>Shows the Details tab when it is chosen; false (and Details idle) otherwise.</summary>
    private bool ShowDetailsContent()
    {
        if (Details == null)
            return false;
        var show = IsDetailsView;
        Details.IsActive = show;
        if (!show)
            return false;
        Details.Rebuild();
        Content = Details;
        return true;
    }

    private void ShowAnthology()
    {
        ShowFilesStrip = false;
        var story = _drilledStoryUuid == null
            ? null
            : _workspace.Nodes.FirstOrDefault(n => n.Uuid == _drilledStoryUuid && !n.Node.IsExcluded);
        if (story == null)
        {
            // A story the planner knows that isn't in the open collection still has a board of its own.
            if (_drilledStoryUuid != null
                && Planner?.Library.FirstOrDefault(s => s.Uuid == _drilledStoryUuid) is { } planned)
            {
                var scope = planned.Story.IsMultiScene ? planned.Story.Lines.Skip(1).ToList() : planned.Story.Lines.ToList();
                RequestBoard(new BoardRequest(_shownRoot ?? "", _drilledStoryUuid, planned.Title, scope));
                if (Details != null)
                    Details.IsActive = false;
                Content = BeatBoard;
                ShowBreadcrumb = true;
                ShowsViewSwitch = false;
                return;
            }
            ShowCorkboard(anthology: true);
            return;
        }

        var scenes = StoryStructure.StoryScenes(_workspace.Nodes, story);
        var request = new BoardRequest(_shownRoot ?? "", story.Uuid, story.Node.Title,
            scenes.Select(s => s.Node.RelativePath).ToList());
        RequestBoard(request, story.Node.Kind == NodeKind.Part ? FindAdoption(request, scenes) : null);
        if (Details != null)
            Details.IsActive = false;
        Content = BeatBoard;
        ShowBreadcrumb = true;
        ShowsViewSwitch = false;
    }

    private void ShowCorkboard(bool anthology = false)
    {
        _drilledStoryUuid = null;
        var planner = anthology && Planner != null;
        ShowsViewSwitch = planner || Details != null;
        if (!ShowDetailsContent())
        {
            Content = planner && !IsCardsView ? Planner! : Corkboard;
            if (planner && !IsCardsView)
                _ = Planner!.RefreshAsync();
        }
        ShowBreadcrumb = false;
        ShowFilesStrip = false;
        if (_requested != null)
        {
            _requested = null;
            _ = TrackAsync(BeatBoard.CloseBoardAsync());
        }
    }

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
