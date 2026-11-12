using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Avalonia;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using ReactiveUI;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

/// <summary>
/// Write mode's Beats panel (Short Story and Anthology projects): the beats of the story being written, kept
/// beside the editor so the plan stays in view while drafting. It follows the editor's selection: a Short
/// Story always shows its one board, an anthology shows the board of the story the selected file belongs to.
/// The beat linked to the open scene is marked current, a beat's scene opens with one click, and titles and
/// notes are edited in place. Adding structure (templates, reordering, linking) stays in Plan mode, which
/// <see cref="PlanRequested"/> opens on this story.
/// <para>
/// It has its own <see cref="BeatBoardViewModel"/>, separate from Plan's, so switching modes never swaps the
/// board Plan shows. When either one saves a board the other has open, the other re-reads it.
/// </para>
/// </summary>
public sealed class WriteBeatsViewModel : ViewModelBase, IDisposable
{
    public const string VisibilitySettingsKey = "beatsPanelVisible";

    private readonly WorkspaceViewModel _workspace;
    private readonly IAppSettingsStore? _settings;
    private readonly INotificationService? _notifications;
    private readonly Subject<string?> _planRequested = new();

    private string? _requestedRoot;
    private string? _requestedKey;
    private int _pendingBoardOps;
    private bool _refreshQueued;
    private bool _disposed;

    public WriteBeatsViewModel(
        WorkspaceViewModel workspace,
        BeatBoardViewModel board,
        BeatBoardViewModel? planBoard = null,
        IAppSettingsStore? settings = null,
        INotificationService? notifications = null)
    {
        _workspace = workspace;
        _settings = settings;
        _notifications = notifications;
        Board = board;

        var available = this.WhenAnyValue(x => x.IsAvailable);
        ToggleCommand = ReactiveCommand.Create(() =>
        {
            IsVisible = !IsVisible;
            _ = PersistVisibilityAsync();
        }, available);
        OpenSceneCommand = ReactiveCommand.Create<BeatCardViewModel>(OpenScene);
        PlanStoryCommand = ReactiveCommand.Create(() => _planRequested.OnNext(StoryUuid), available);
        foreach (var command in new IHandleObservableErrors[] { ToggleCommand, OpenSceneCommand, PlanStoryCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications?.ShowError(ex.Message)));

        _isOpen = this.WhenAnyValue(x => x.IsAvailable, x => x.IsVisible, (a, v) => a && v)
            .ToProperty(this, x => x.IsOpen);
        Disposables.Add(_isOpen);

        _hasBeats = Board.WhenAnyValue(x => x.IsEmpty, x => x.BoardKey, (empty, key) => key != null && !empty)
            .ToProperty(this, x => x.HasBeats);
        Disposables.Add(_hasBeats);

        _showsEmptyState = Board.WhenAnyValue(x => x.IsEmpty, x => x.BoardKey, (empty, key) => key != null && empty)
            .ToProperty(this, x => x.ShowsEmptyState);
        Disposables.Add(_showsEmptyState);

        // The open file, the book and the project kind all decide which story's board to show. Changes
        // are coalesced into one refresh after they settle (a reload empties the node list before refilling it).
        Disposables.Add(
            Observable.Merge(
                    workspace.WhenAnyValue(x => x.Profile).Select(_ => Unit.Default),
                    workspace.WhenAnyValue(x => x.HasWorkspace).Select(_ => Unit.Default),
                    workspace.WhenAnyValue(x => x.SelectedNode).Select(_ => Unit.Default),
                    workspace.WorkspaceChanged.Select(_ => Unit.Default),
                    workspace.Nodes.ToObservableChangeSet().AutoRefresh(n => n.Node).Select(_ => Unit.Default))
                .Subscribe(_ => QueueRefresh()));

        // The current-beat marker follows the open scene and the cards (a reload replaces them).
        Disposables.Add(
            Observable.Merge(
                    workspace.WhenAnyValue(x => x.SelectedNode).Select(_ => Unit.Default),
                    CollectionChanges(Board.Beats),
                    Board.Beats.ToObservableChangeSet().AutoRefresh(c => c.ScenePath).Select(_ => Unit.Default))
                .Subscribe(_ => MarkCurrentBeats()));

        // Plan and Write show the same files through two boards: when one saves, the other re-reads.
        if (planBoard != null)
        {
            Disposables.Add(planBoard.Saved
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(saved => _ = Board.ReloadIfShowingAsync(saved.Root, saved.Key)));
            Disposables.Add(Board.Saved
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(saved => _ = planBoard.ReloadIfShowingAsync(saved.Root, saved.Key)));
        }
    }

    public BeatBoardViewModel Board { get; }

    /// <summary>
    /// Fires when the author asks to plan the story in Plan mode: the anthology story's registry id, or null
    /// for a Short Story (its one board).
    /// </summary>
    public IObservable<string?> PlanRequested => _planRequested.AsObservable();

    private bool _isAvailable;
    /// <summary>True when a Short Story or Anthology workspace is open, so the panel can be shown.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isAvailable, value);
    }

    private bool _isVisible = true;
    /// <summary>The author's choice to show the panel (F7); kept across launches. Shown by default.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _isOpen;
    /// <summary>True when the panel's content is shown: available and chosen.</summary>
    public bool IsOpen => _isOpen.Value;

    private readonly ObservableAsPropertyHelper<bool> _hasBeats;
    public bool HasBeats => _hasBeats.Value;

    private readonly ObservableAsPropertyHelper<bool> _showsEmptyState;
    /// <summary>True when the story's board is open and has no beats yet.</summary>
    public bool ShowsEmptyState => _showsEmptyState.Value;

    private bool _hasStory;
    /// <summary>False when the open file belongs to no story with a board (nothing selected, or excluded).</summary>
    public bool HasStory
    {
        get => _hasStory;
        private set => this.RaiseAndSetIfChanged(ref _hasStory, value);
    }

    private string _storyTitle = "";
    /// <summary>Title of the story whose beats are shown.</summary>
    public string StoryTitle
    {
        get => _storyTitle;
        private set => this.RaiseAndSetIfChanged(ref _storyTitle, value);
    }

    private string? _storyUuid;
    /// <summary>Anthology: registry id of the story shown; null for a Short Story.</summary>
    public string? StoryUuid
    {
        get => _storyUuid;
        private set => this.RaiseAndSetIfChanged(ref _storyUuid, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    /// <summary>Opens the scene file a beat is linked to in the editor.</summary>
    public ReactiveCommand<BeatCardViewModel, Unit> OpenSceneCommand { get; }
    /// <summary>Raises <see cref="PlanRequested"/> for the story shown.</summary>
    public ReactiveCommand<Unit, Unit> PlanStoryCommand { get; }

    /// <summary>Writes the panel's pending beat edits; true when nothing is left unsaved.</summary>
    public Task<bool> FlushAsync() => Board.FlushAsync();

    // ── Visibility ────────────────────────────────────────────────────────────

    public async Task RestoreSettingsAsync()
    {
        if (_settings == null)
            return;
        try
        {
            var stored = await _settings.GetAsync<bool?>(VisibilitySettingsKey).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => IsVisible = stored ?? true);
        }
        catch
        {
            // Non-fatal: the panel keeps its default.
        }
    }

    /// <summary>Sets <see cref="IsVisible"/> directly (the shell's Reset Layout).</summary>
    public void ApplyVisibility(bool visible) => IsVisible = visible;

    public async Task PersistVisibilityAsync()
    {
        if (_settings == null)
            return;
        try
        {
            await _settings.SetAsync(VisibilitySettingsKey, IsVisible).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; the choice may not survive a restart.
        }
    }

    // ── Following the editor ──────────────────────────────────────────────────

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
        IsAvailable = _workspace.HasWorkspace && profile.UsesBeatBoard;

        var story = IsAvailable ? CurrentStory(profile.Kind) : null;
        HasStory = story != null;
        StoryTitle = story?.Title ?? "";
        StoryUuid = story?.Story?.Uuid;

        if (story == null)
        {
            CloseBoard();
            return;
        }

        var root = _workspace.WorkspaceRoot;
        if (_requestedKey == story.Key && _requestedRoot == root && Board.BoardKey == story.Key && _pendingBoardOps == 0)
        {
            Board.UpdateScope(story.Title, story.Scope);
            return;
        }
        if (_requestedKey == story.Key && _requestedRoot == root)
            return; // already on its way

        _requestedKey = story.Key;
        _requestedRoot = root;
        _ = TrackAsync(Board.OpenBoardAsync(story.Key, story.Title, story.Scope));
    }

    private StoryStructure.StoryBoardRef? CurrentStory(ProjectKind kind)
    {
        var nodes = _workspace.Nodes;
        if (kind == ProjectKind.Story)
            return StoryStructure.ShortStory(nodes);

        if (_workspace.SelectedNode is not { } selected)
            return null;
        var story = StoryStructure.AnthologyStoryOf(nodes, selected);
        return story == null ? null : StoryStructure.AnthologyStory(nodes, story);
    }

    private void CloseBoard()
    {
        if (_requestedKey == null)
            return;
        _requestedKey = null;
        _requestedRoot = null;
        _ = TrackAsync(Board.CloseBoardAsync());
    }

    /// <summary>
    /// Waits for a board open or close. The board closes itself when its workspace closes, which can cancel
    /// an open asked for here; once nothing is in flight, a board that isn't the one asked for is asked again.
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
            _notifications?.ShowError($"Failed to open the story's beats: {ex.Message}");
        }
        finally
        {
            _pendingBoardOps--;
        }

        if (_disposed || _pendingBoardOps > 0 || _requestedKey == null || Board.BoardKey == _requestedKey)
            return;
        _requestedKey = null;
        _requestedRoot = null;
        QueueRefresh();
    }

    // ── Current beat and opening scenes ───────────────────────────────────────

    private void MarkCurrentBeats()
    {
        var open = _workspace.SelectedNode?.Node;
        foreach (var card in Board.Beats)
            card.IsCurrent = open is { Kind: NodeKind.Chapter } && card.ScenePath is { } path
                && BeatCardViewModel.IsSameScene(open, path);
    }

    private void OpenScene(BeatCardViewModel card)
    {
        if (card.ScenePath is not { } path || card.IsSceneMissing)
            return;
        if (!_workspace.SelectChapterByPath(path))
            _notifications?.ShowInfo($"'{card.SceneTitle ?? path}' is no longer in the book.");
    }

    private static IObservable<Unit> CollectionChanges(INotifyCollectionChanged collection) =>
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => collection.CollectionChanged += h,
                h => collection.CollectionChanged -= h)
            .Select(_ => Unit.Default);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disposables.Dispose();
        _planRequested.OnCompleted();
        _planRequested.Dispose();
    }
}
