using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using DynamicData.Binding;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

/// <summary>A scene a beat on the open board may be linked to.</summary>
public sealed record SceneOption(string RelativePath, string Title);

/// <summary>A confirmation the board asks of the author: dialog title, message and confirm button text.</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText);

/// <summary>Applies template <paramref name="Key"/>; <paramref name="Parameter"/> is the template's parameter value (MICE thread).</summary>
public sealed record ApplyTemplateRequest(string Key, TemplateApplyMode Mode, string? Parameter);

/// <summary>
/// Logic behind a beat board: one story's ordered beat cards, their debounced auto-save, templates,
/// reordering, deleting, and links to the story's scenes. One board is open at a time; opening
/// another (or closing, or the workspace closing) first writes pending edits to the board they were
/// made on. A board whose file can't be read is read-only for the session and is never written.
/// Beats are read and written through the workspace's <see cref="IStoryMetadataStore"/> (an embedded story's
/// board lives in its file); board moves are not done here.
/// </summary>
public sealed class BeatBoardViewModel : ViewModelBase, IDisposable
{
    public const string NewBeatTitle = "New beat";

    /// <summary>The scene picker's "not linked" option.</summary>
    public static readonly SceneOption NoScene = new("", "None");

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Where a board's saves go: the workspace and key it was loaded from, and the store that loaded it (used
    /// only once that workspace is no longer open; while it is, saves use its current store).
    /// </summary>
    private sealed record BoardTarget(string Root, string Key, IStoryMetadataStore LoadedFrom);

    private readonly WorkspaceViewModel _workspace;
    private readonly INotificationService _notifications;

    private readonly ObservableCollectionExtended<BeatCardViewModel> _beats = new();
    private readonly Dictionary<BeatCardViewModel, IDisposable> _cardSubscriptions = new();
    private readonly Subject<Unit> _saveRequests = new();

    /// <summary>Workspace root of the open board (read-only ones too); null when no board is open.</summary>
    private string? _boardRoot;
    /// <summary>Where edits are saved; null when no board is open or the open one is read-only.</summary>
    private BoardTarget? _target;
    private IReadOnlyList<string> _sceneScope = [];
    private bool _savePending;
    /// <summary>True when the open board's last save failed; a failure on a board since closed doesn't count.</summary>
    private bool _lastSaveFailed;
    private Task _saveChain = Task.CompletedTask;
    private int _generation;
    private Task _currentSwitch = Task.CompletedTask;

    public BeatBoardViewModel(
        WorkspaceViewModel workspace,
        INotificationService notifications,
        IScheduler? saveScheduler = null)
    {
        _workspace = workspace;
        _notifications = notifications;
        Beats = new ReadOnlyObservableCollection<BeatCardViewModel>(_beats);

        _isEmpty = CollectionChanges(_beats)
            .Select(_ => _beats.Count == 0)
            .StartWith(true)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.IsEmpty);
        Disposables.Add(_isEmpty);

        _canEdit = this.WhenAnyValue(x => x.BoardKey, x => x.IsReadOnly, (key, readOnly) => key != null && !readOnly)
            .ToProperty(this, x => x.CanEdit);
        Disposables.Add(_canEdit);

        _showsTemplatePicker = this.WhenAnyValue(x => x.CanEdit, x => x.IsEmpty, x => x.IsChoosingTemplate,
                (edit, empty, choosing) => edit && (empty || choosing))
            .ToProperty(this, x => x.ShowsTemplatePicker);
        Disposables.Add(_showsTemplatePicker);

        _showsBeats = this.WhenAnyValue(x => x.IsEmpty, x => x.ShowsTemplatePicker, (empty, picker) => !empty && !picker)
            .ToProperty(this, x => x.ShowsBeats);
        Disposables.Add(_showsBeats);

        _showsSceneLinks = this.WhenAnyValue(x => x.SceneOptions, options => options.Count > 1)
            .ToProperty(this, x => x.ShowsSceneLinks);
        Disposables.Add(_showsSceneLinks);

        var canEdit = this.WhenAnyValue(x => x.CanEdit);
        var canDelete = this.WhenAnyValue(x => x.CanEdit, x => x.Selected, (edit, selected) => edit && selected != null);

        AddBeatCommand = ReactiveCommand.CreateFromTask(AddBeatAsync, canEdit);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, canDelete);
        DeleteBeatCommand = ReactiveCommand.CreateFromTask<BeatCardViewModel>(DeleteBeatAsync, canEdit);
        MoveBeatCommand = ReactiveCommand.CreateFromTask<(int From, int To)>(m => MoveBeatAsync(m.From, m.To), canEdit);
        ApplyTemplateCommand = ReactiveCommand.CreateFromTask<ApplyTemplateRequest>(ApplyTemplateAsync, canEdit);
        LinkSceneCommand = ReactiveCommand.CreateFromTask<(string BeatId, string? RelativePath)>(
            l => LinkSceneAsync(l.BeatId, l.RelativePath), canEdit);
        ChooseTemplateCommand = ReactiveCommand.Create(() => { IsChoosingTemplate = true; },
            this.WhenAnyValue(x => x.CanEdit, x => x.IsEmpty, (edit, empty) => edit && !empty));
        CancelTemplateChoiceCommand = ReactiveCommand.Create(() => { IsChoosingTemplate = false; });

        foreach (var command in new IHandleObservableErrors[]
                     { AddBeatCommand, DeleteSelectedCommand, DeleteBeatCommand, MoveBeatCommand, ApplyTemplateCommand, LinkSceneCommand,
                       ChooseTemplateCommand, CancelTemplateChoiceCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications.ShowError(ex.Message)));

        Disposables.Add(
            _saveRequests
                .Throttle(SaveDelay, saveScheduler ?? AvaloniaScheduler.Instance)
                .Subscribe(unit => _ = FlushAsync()));

        // Cards and scene links are only touched on the UI thread.
        Disposables.Add(
            workspace.WorkspaceChanged
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(unit => _ = CloseIfWorkspaceChangedAsync()));

        Disposables.Add(
            CollectionChanges(workspace.Nodes)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => RefreshScenes()));
    }

    // ── State ─────────────────────────────────────────────────────────────────

    public ReadOnlyObservableCollection<BeatCardViewModel> Beats { get; }

    private string? _boardKey;
    /// <summary>Key of the open board (also when it is read-only); null when no board is open.</summary>
    public string? BoardKey
    {
        get => _boardKey;
        private set => this.RaiseAndSetIfChanged(ref _boardKey, value);
    }

    private string _boardTitle = "";
    public string BoardTitle
    {
        get => _boardTitle;
        private set => this.RaiseAndSetIfChanged(ref _boardTitle, value);
    }

    private bool _isReadOnly;
    /// <summary>True when the open board's file couldn't be read; nothing is written to it.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;

    private readonly ObservableAsPropertyHelper<bool> _canEdit;
    /// <summary>True when a board is open and writable.</summary>
    public bool CanEdit => _canEdit.Value;

    private readonly ObservableAsPropertyHelper<bool> _showsTemplatePicker;
    /// <summary>
    /// True when the template gallery is shown in place of the beats: a writable board with no beats, or
    /// one whose author chose Apply template.
    /// </summary>
    public bool ShowsTemplatePicker => _showsTemplatePicker.Value;

    private readonly ObservableAsPropertyHelper<bool> _showsBeats;
    /// <summary>True when the beat cards are shown: the board has beats and the gallery isn't open.</summary>
    public bool ShowsBeats => _showsBeats.Value;

    private bool _isChoosingTemplate;
    /// <summary>
    /// True while the author picks a template to add to (or replace) a board that already has beats.
    /// Cleared when a template is applied, the choice is cancelled, or the board changes.
    /// </summary>
    public bool IsChoosingTemplate
    {
        get => _isChoosingTemplate;
        private set => this.RaiseAndSetIfChanged(ref _isChoosingTemplate, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _showsSceneLinks;
    /// <summary>
    /// True when the story has two or more scene files, so a beat can say which one it happens in. A story
    /// told in one file has nothing to choose between.
    /// </summary>
    public bool ShowsSceneLinks => _showsSceneLinks.Value;

    private BeatCardViewModel? _selected;
    public BeatCardViewModel? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public IReadOnlyList<BeatTemplate> Templates => BeatTemplateLoader.All;

    /// <summary>The template picker's entries, one per <see cref="Templates"/> item, in order.</summary>
    public IReadOnlyList<BeatTemplateOptionViewModel> TemplateOptions { get; } =
        BeatTemplateLoader.All.Select(t => new BeatTemplateOptionViewModel(t)).ToList();

    private IReadOnlyList<SceneOption> _sceneOptions = [];
    /// <summary>Scenes a beat on this board may be linked to, in scope order.</summary>
    public IReadOnlyList<SceneOption> SceneOptions
    {
        get => _sceneOptions;
        private set => this.RaiseAndSetIfChanged(ref _sceneOptions, value);
    }

    /// <summary>Asks the author to confirm a deletion or replacement; defaults to "no".</summary>
    public Func<ConfirmRequest, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);

    public ReactiveCommand<Unit, Unit> AddBeatCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    /// <summary>Selects a beat, then deletes it as <see cref="DeleteSelectedCommand"/> does.</summary>
    public ReactiveCommand<BeatCardViewModel, Unit> DeleteBeatCommand { get; }
    public ReactiveCommand<(int From, int To), Unit> MoveBeatCommand { get; }
    public ReactiveCommand<ApplyTemplateRequest, Unit> ApplyTemplateCommand { get; }
    /// <summary>Links a beat to a scene in scope; a null path unlinks it.</summary>
    public ReactiveCommand<(string BeatId, string? RelativePath), Unit> LinkSceneCommand { get; }
    /// <summary>Shows the template gallery over a board that has beats, to add or replace them.</summary>
    public ReactiveCommand<Unit, Unit> ChooseTemplateCommand { get; }
    /// <summary>Closes the template gallery and goes back to the beats.</summary>
    public ReactiveCommand<Unit, Unit> CancelTemplateChoiceCommand { get; }

    // ── Opening and closing ───────────────────────────────────────────────────

    /// <summary>
    /// Writes the open board's pending edits to that board, then opens <paramref name="boardKey"/> in the
    /// current workspace. <paramref name="sceneScope"/> lists the RelativePaths its beats may link to.
    /// </summary>
    public Task OpenBoardAsync(string boardKey, string title, IReadOnlyList<string> sceneScope)
    {
        var open = OpenCoreAsync(++_generation, boardKey, title ?? "", sceneScope.ToList());
        _currentSwitch = open;
        return open;
    }

    /// <summary>
    /// Updates the open board's title and the scenes its beats may link to (the story was renamed or its
    /// scenes reordered) without reopening it, so cards, selection and pending edits stay.
    /// </summary>
    public void UpdateScope(string title, IReadOnlyList<string> sceneScope)
    {
        if (_disposed || BoardKey == null)
            return;
        BoardTitle = title ?? "";
        _sceneScope = sceneScope.ToList();
        RefreshScenes();
    }

    /// <summary>Writes pending edits, then closes the board.</summary>
    public Task CloseBoardAsync()
    {
        var close = CloseCoreAsync(++_generation);
        _currentSwitch = close;
        return close;
    }

    private async Task OpenCoreAsync(int generation, string key, string title, IReadOnlyList<string> scope)
    {
        while (true)
        {
            await FlushAsync();
            if (_disposed)
                return;
            if (generation != _generation)
            {
                await _currentSwitch;
                return;
            }
            // Edits made during the flush belong to the open board; write them before replacing it.
            if (_savePending)
                continue;

            var root = _workspace.WorkspaceRoot;
            if (!_workspace.HasWorkspace || string.IsNullOrEmpty(root))
            {
                ClearBoard();
                return;
            }

            var store = _workspace.MetadataStore;
            var result = await store.LoadBeatsAsync(root, key);
            if (_disposed)
                return;
            if (generation != _generation)
            {
                await _currentSwitch;
                return;
            }

            // Edits made while reading are flushed (to the board they were made on) and the file
            // re-read, so replacing the cards never drops them. Nothing below awaits.
            if (_savePending)
                continue;

            // The workspace changed while reading: this board belongs to one that is no longer open.
            if (root != _workspace.WorkspaceRoot)
            {
                ClearBoard();
                return;
            }

            ClearBoard();
            _boardRoot = root;
            _sceneScope = scope;
            BoardTitle = title;
            if (result.IsSuccess)
            {
                _target = new BoardTarget(root, key, store);
                foreach (var beat in result.Value!)
                    _beats.Add(CreateCard(beat));
            }
            else
            {
                _notifications.ShowInfo($"{result.Error} This beat board is read-only until it is fixed.");
            }
            IsReadOnly = !result.IsSuccess;
            BoardKey = key;
            RefreshScenes();
            return;
        }
    }

    private async Task CloseCoreAsync(int generation)
    {
        while (true)
        {
            await FlushAsync();
            if (_disposed)
                return;
            if (generation != _generation)
            {
                await _currentSwitch;
                return;
            }
            if (_savePending)
                continue;

            ClearBoard();
            return;
        }
    }

    /// <summary>Closes the board once its workspace is no longer the open one.</summary>
    private async Task CloseIfWorkspaceChangedAsync()
    {
        if (_boardRoot == null || _boardRoot == _workspace.WorkspaceRoot)
            return;
        try
        {
            await CloseBoardAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Failed to close the beat board: {ex.Message}");
        }
    }

    /// <summary>Callers check no save is pending first, so no edit to the outgoing cards is lost.</summary>
    private void ClearBoard()
    {
        _target = null;
        _boardRoot = null;
        _lastSaveFailed = false;
        _sceneScope = [];
        Selected = null;
        foreach (var card in _beats)
            DisposeCard(card);
        _beats.Clear();
        BoardKey = null;
        BoardTitle = "";
        IsReadOnly = false;
        IsChoosingTemplate = false;
        SceneOptions = [];
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Adds a beat after <see cref="Selected"/> (or at the end) and selects it.</summary>
    public async Task AddBeatAsync()
    {
        if (_target == null)
            return;

        var selectedIndex = Selected == null ? -1 : _beats.IndexOf(Selected);
        var index = selectedIndex >= 0 ? selectedIndex + 1 : _beats.Count;
        var card = CreateCard(Beat.Create(NewBeatTitle));
        _beats.Insert(index, card);
        Selected = card;
        await SaveNowAsync();
    }

    public async Task DeleteSelectedAsync()
    {
        var card = Selected;
        var target = _target;
        if (card == null || target == null)
            return;

        var name = string.IsNullOrWhiteSpace(card.Title) ? "this beat" : $"\"{card.Title}\"";
        if (!await ConfirmAsync(new ConfirmRequest("Delete Beat", $"Delete {name}? This can't be undone.", "Delete")))
            return;
        // Another board opened (or this one closed) while asking.
        if (_disposed || !ReferenceEquals(target, _target) || !_beats.Contains(card))
            return;

        if (ReferenceEquals(Selected, card))
            Selected = null;
        _beats.Remove(card);
        DisposeCard(card);
        await SaveNowAsync();
    }

    public Task DeleteBeatAsync(BeatCardViewModel card)
    {
        if (_target == null || !_beats.Contains(card))
            return Task.CompletedTask;
        Selected = card;
        return DeleteSelectedAsync();
    }

    /// <summary>Moves the beat at <paramref name="from"/> to <paramref name="to"/> (clamped), as <see cref="BeatBoardOperations.Move"/> does.</summary>
    public async Task MoveBeatAsync(int from, int to)
    {
        if (_target == null || from < 0 || from >= _beats.Count)
            return;
        to = Math.Clamp(to, 0, _beats.Count - 1);
        if (to == from)
            return;

        _beats.Move(from, to);
        await SaveNowAsync();
    }

    /// <summary>Appends a template's beats, or replaces every beat with them after the author confirms.</summary>
    public async Task ApplyTemplateAsync(ApplyTemplateRequest request)
    {
        var target = _target;
        if (target == null)
            return;

        var template = BeatTemplateLoader.Find(request.Key);
        if (template == null)
        {
            _notifications.ShowError($"There is no beat template '{request.Key}'.");
            return;
        }

        IReadOnlyList<Beat> templateBeats;
        try
        {
            templateBeats = template.Instantiate(request.Parameter);
        }
        catch (ArgumentException ex)
        {
            _notifications.ShowError(ex.Message);
            return;
        }

        if (request.Mode == TemplateApplyMode.Replace && _beats.Count > 0)
        {
            var count = _beats.Count == 1 ? "the 1 beat" : $"all {_beats.Count} beats";
            if (!await ConfirmAsync(new ConfirmRequest("Replace Beats",
                    $"Replace {count} on this board with {template.Name}? This can't be undone.", "Replace")))
                return;
            if (_disposed || !ReferenceEquals(target, _target))
                return;
        }

        // Appending a template without beats (Blank) changes nothing.
        if (request.Mode == TemplateApplyMode.Append && templateBeats.Count == 0)
            return;

        var updated = BeatBoardOperations.ApplyTemplate(Snapshot(), templateBeats, request.Mode);
        SetCards(updated);
        IsChoosingTemplate = false;
        await SaveNowAsync();
    }

    /// <summary>Links beat <paramref name="beatId"/> to a scene in scope, or unlinks it when <paramref name="relativePath"/> is null.</summary>
    public async Task LinkSceneAsync(string beatId, string? relativePath)
    {
        if (_target == null)
            return;
        var card = _beats.FirstOrDefault(c => c.Id == beatId);
        if (card == null)
            return;

        if (relativePath != null)
        {
            var inScope = _sceneScope.FirstOrDefault(p => string.Equals(p, relativePath, StringComparison.OrdinalIgnoreCase));
            if (inScope == null)
            {
                _notifications.ShowError("That scene isn't part of this story.");
                return;
            }
            relativePath = inScope;
        }

        var scene = relativePath == null ? null : FindScene(null, relativePath);
        var sceneId = scene?.Uuid;
        if (string.Equals(card.ScenePath, relativePath, StringComparison.Ordinal)
            && string.Equals(card.SceneId, sceneId, StringComparison.Ordinal))
            return;

        card.SetScene(relativePath, sceneId);
        card.AttachScene(scene);
        await SaveNowAsync();
    }

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>Writes pending edits now and waits for queued saves; true when nothing is left unsaved.</summary>
    public async Task<bool> FlushAsync()
    {
        var saves = _savePending ? SaveNowAsync() : _saveChain;
        await saves;
        return !_savePending && !_lastSaveFailed;
    }

    private Task SaveNowAsync()
    {
        _savePending = false;
        if (_target is not { } target)
            return _saveChain;

        // The target and snapshot are captured together, so a save always goes to the board it shows.
        _saveChain = SaveAfterAsync(_saveChain, target, Snapshot());
        return _saveChain;
    }

    private async Task SaveAfterAsync(Task previous, BoardTarget target, IReadOnlyList<Beat> snapshot)
    {
        await previous;
        try
        {
            var store = target.Root == _workspace.WorkspaceRoot ? _workspace.MetadataStore : target.LoadedFrom;
            var result = await store.SaveBeatsAsync(target.Root, target.Key, snapshot);
            RecordSave(target, result.IsSuccess);
            if (!result.IsSuccess)
                _notifications.ShowError(result.Error!);
        }
        catch (Exception ex)
        {
            RecordSave(target, succeeded: false);
            _notifications.ShowError($"Failed to save beats: {ex.Message}");
        }
    }

    private void RecordSave(BoardTarget target, bool succeeded)
    {
        if (ReferenceEquals(target, _target))
            _lastSaveFailed = !succeeded;
    }

    private void OnCardEdited()
    {
        if (_disposed || _target == null)
            return;
        _savePending = true;
        _saveRequests.OnNext(Unit.Default);
    }

    private IReadOnlyList<Beat> Snapshot() => _beats.Select(c => c.ToBeat()).ToList();

    // ── Cards and scenes ──────────────────────────────────────────────────────

    private BeatCardViewModel CreateCard(Beat beat)
    {
        var card = new BeatCardViewModel(beat);
        _cardSubscriptions[card] = card.Edited.Merge(card.SceneMoved).Subscribe(_ => OnCardEdited());
        if (AttachCard(card))
            OnCardEdited();
        card.SetSceneChoices(SceneChoices(), path => LinkFromPicker(card, path));
        return card;
    }

    private void DisposeCard(BeatCardViewModel card)
    {
        if (_cardSubscriptions.Remove(card, out var subscription))
            subscription.Dispose();
        card.Dispose();
    }

    /// <summary>Shows <paramref name="beats"/> in order, keeping the cards of beats already shown.</summary>
    private void SetCards(IReadOnlyList<Beat> beats)
    {
        var existing = _beats.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var cards = beats
            .Select(b => existing.Remove(b.Id, out var card) ? card : CreateCard(b))
            .ToList();
        if (Selected is { } selected && existing.ContainsKey(selected.Id))
            Selected = null;
        _beats.Load(cards);
        foreach (var removed in existing.Values)
            DisposeCard(removed);
    }

    /// <summary>The chapter a link points at: by registry id first (it survives renames), then by path.</summary>
    private ChapterViewModel? FindScene(string? sceneId, string? relativePath)
    {
        if (sceneId != null
            && _workspace.Nodes.FirstOrDefault(n => n.Uuid == sceneId && n.Node.Kind == NodeKind.Chapter && !n.Node.IsExcluded)
                is { } byId)
            return byId;
        return relativePath == null
            ? null
            : _workspace.Nodes.FirstOrDefault(n => BeatCardViewModel.IsSameScene(n.Node, relativePath));
    }

    /// <summary>
    /// Attaches <paramref name="card"/> to its scene. Returns true when the scene, found by id, was renamed
    /// and the card now holds its new path, which needs saving.
    /// </summary>
    private bool AttachCard(BeatCardViewModel card)
    {
        var scene = FindScene(card.SceneId, card.ScenePath);
        var moved = false;
        if (scene != null)
        {
            if (card.SceneId == scene.Uuid)
                moved = card.FollowScene(scene);
            else
                card.RememberSceneId(scene.Uuid);
        }
        card.AttachScene(scene);
        return moved;
    }

    /// <summary>Re-attaches every card to its scene and refreshes the scene options, after the book changed.</summary>
    private void RefreshScenes()
    {
        if (_disposed)
            return;
        var moved = false;
        foreach (var card in _beats)
            moved |= AttachCard(card);
        SceneOptions = _sceneScope
            .Select(path => new SceneOption(path, FindScene(null, path)?.Node.Title ?? path))
            .ToList();
        var choices = SceneChoices();
        foreach (var card in _beats)
            card.SetSceneChoices(choices, path => LinkFromPicker(card, path));
        if (moved)
            OnCardEdited();
    }

    private IReadOnlyList<SceneOption> SceneChoices() => [NoScene, .. SceneOptions];

    private void LinkFromPicker(BeatCardViewModel card, string? relativePath) =>
        LinkSceneCommand.Execute((card.Id, relativePath)).Subscribe(_ => { }, _ => { /* reported via ThrownExceptions */ });

    private static IObservable<Unit> CollectionChanges(INotifyCollectionChanged collection) =>
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => collection.CollectionChanged += h,
                h => collection.CollectionChanged -= h)
            .Select(_ => Unit.Default);

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disposables.Dispose();
        foreach (var card in _beats)
            DisposeCard(card);
        _beats.Clear();
        _saveRequests.OnCompleted();
        _saveRequests.Dispose();
    }
}
