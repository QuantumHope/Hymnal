using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using ReactiveUI.Avalonia;
using DynamicData.Binding;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

/// <summary>
/// Logic behind the Ideas screen: the filtered idea card list, the side editor's debounced
/// auto-save, and creating, deleting and promoting ideas. Ideas exist only for project
/// kinds whose profile <see cref="ProjectProfile.ShowsIdeas"/>; for others the list is empty.
/// </summary>
public sealed class IdeasViewModel : ViewModelBase, IDisposable
{
    public const string ReadOnlyNotice = "Couldn't read ideas.json — ideas are read-only until it is fixed.";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly WorkspaceViewModel _workspace;
    private readonly IStoryIdeasService _ideas;
    private readonly StoryIdeaPromoter _promoter;
    private readonly INotificationService _notifications;

    /// <summary>Every loaded idea, regardless of filter.</summary>
    private readonly List<IdeaCardViewModel> _allCards = new();
    private readonly Dictionary<IdeaCardViewModel, IDisposable> _cardSubscriptions = new();
    private readonly ObservableCollectionExtended<IdeaCardViewModel> _cards = new();
    private readonly Subject<Unit> _saveRequests = new();
    private readonly Subject<Unit> _loadedRootChanged = new();
    private readonly Subject<string> _storyOpened = new();

    /// <summary>Workspace root the current ideas were loaded from; null when nothing is loaded.</summary>
    private string? _loadedRoot;
    private bool _savePending;
    private bool _lastSaveFailed;
    private Task _saveChain = Task.CompletedTask;
    private ProjectKind? _loadedKind;
    private int _loadGeneration;
    private Task _currentLoad = Task.CompletedTask;
    private Task _promoteCompletion = Task.CompletedTask;

    public IdeasViewModel(
        WorkspaceViewModel workspace,
        IStoryIdeasService ideas,
        StoryIdeaPromoter promoter,
        INotificationService notifications,
        IScheduler? saveScheduler = null)
    {
        _workspace = workspace;
        _ideas = ideas;
        _promoter = promoter;
        _notifications = notifications;
        Cards = new ReadOnlyObservableCollection<IdeaCardViewModel>(_cards);

        var profile = workspace.WhenAnyValue(w => w.Profile);

        var selectedState = this.WhenAnyValue(x => x.Selected)
            .Select(card => card == null
                ? Observable.Return<IdeaState?>(null)
                : card.WhenAnyValue(c => c.State).Select(s => (IdeaState?)s))
            .Switch();

        // Edits from elsewhere (or a reload) reach the pickers too.
        Disposables.Add(
            this.WhenAnyValue(x => x.Selected)
                .Select(card => card == null
                    ? Observable.Return(Unit.Default)
                    : card.WhenAnyValue(c => c.State, c => c.TargetPreset).Select(_ => Unit.Default))
                .Switch()
                .Subscribe(_ =>
                {
                    this.RaisePropertyChanged(nameof(SelectedState));
                    this.RaisePropertyChanged(nameof(SelectedTarget));
                }));

        // While another workspace's ideas are still shown (its load is in flight), nothing is promoted.
        var showsOpenWorkspace = Observable.Merge(workspace.WorkspaceChanged.Select(_ => Unit.Default), _loadedRootChanged)
            .StartWith(Unit.Default)
            .Select(_ => ShowsOpenWorkspace);

        var readOnlyOrPromoting = this.WhenAnyValue(x => x.IsReadOnly, x => x.IsPromoting, (r, p) => r || p);

        _canEdit = readOnlyOrPromoting.Select(blocked => !blocked).ToProperty(this, x => x.CanEdit);
        Disposables.Add(_canEdit);

        _canPromote = Observable.CombineLatest(
                profile, selectedState, readOnlyOrPromoting, showsOpenWorkspace,
                (p, state, blocked, current) =>
                    p.Kind == ProjectKind.Anthology && state is { } s && s != IdeaState.Used
                    && Selected?.StoryId == null && !blocked && current)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.CanPromote);
        Disposables.Add(_canPromote);

        _showsPromote = profile
            .Select(p => p.Kind == ProjectKind.Anthology)
            .ToProperty(this, x => x.ShowsPromote);
        Disposables.Add(_showsPromote);

        var canCreate = Observable.CombineLatest(
            profile, readOnlyOrPromoting,
            (p, blocked) => p.ShowsIdeas && !blocked);
        var canDelete = Observable.CombineLatest(
            this.WhenAnyValue(x => x.Selected), readOnlyOrPromoting,
            (selected, blocked) => selected != null && !blocked);

        NewIdeaCommand = ReactiveCommand.CreateFromTask<(string Title, string Premise)>(
            request => NewIdeaAsync(request.Title, request.Premise), canCreate);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, canDelete);
        PromoteSelectedCommand = ReactiveCommand.CreateFromTask(
            PromoteSelectedAsync, this.WhenAnyValue(x => x.CanPromote));
        ClearTagFilterCommand = ReactiveCommand.Create(() => { TagFilter = null; });

        foreach (var command in new IHandleObservableErrors[] { NewIdeaCommand, DeleteSelectedCommand, PromoteSelectedCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications.ShowError(ex.Message)));

        Disposables.Add(
            _saveRequests
                .Throttle(SaveDelay, saveScheduler ?? AvaloniaScheduler.Instance)
                .Subscribe(unit => _ = FlushAsync()));

        // Moving to another card writes the previous card's pending edits right away.
        Disposables.Add(
            this.WhenAnyValue(x => x.Selected)
                .Skip(1)
                .Subscribe(unit => _ = FlushAsync()));

        Disposables.Add(
            this.WhenAnyValue(x => x.TagFilter, x => x.ShowUsedAndShelved)
                .Skip(1)
                .Subscribe(_ => RefreshCards()));

        // Card collections are only touched on the UI thread.
        Disposables.Add(
            Observable.Merge(workspace.WorkspaceChanged.Select(_ => Unit.Default), profile.Select(_ => Unit.Default))
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(unit => _ = ReloadIfWorkspaceChangedAsync()));
    }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Ideas passing the current filters, newest first, then by title.</summary>
    public ReadOnlyObservableCollection<IdeaCardViewModel> Cards { get; }

    private IReadOnlyList<string> _allTags = [];
    /// <summary>Every tag used by any loaded idea, sorted.</summary>
    public IReadOnlyList<string> AllTags
    {
        get => _allTags;
        private set => this.RaiseAndSetIfChanged(ref _allTags, value);
    }

    private string? _tagFilter;
    /// <summary>Show only ideas carrying this tag (case-insensitive); null or empty shows all.</summary>
    public string? TagFilter
    {
        get => _tagFilter;
        set => this.RaiseAndSetIfChanged(ref _tagFilter, value);
    }

    private bool _showUsedAndShelved;
    public bool ShowUsedAndShelved
    {
        get => _showUsedAndShelved;
        set => this.RaiseAndSetIfChanged(ref _showUsedAndShelved, value);
    }

    private IdeaCardViewModel? _selected;
    public IdeaCardViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
                return;
            UpdateEditorPickers(() => this.RaiseAndSetIfChanged(ref _selected, value));
        }
    }

    // ── Editor pickers ────────────────────────────────────────────────────────
    // The state picker's options differ per card (a used idea also lists Used). While a picker's
    // options are being swapped it can push a value meant for neither card, so picker writes are
    // ignored until the swap is done, and the real value is pushed back afterwards.

    private bool _updatingEditorPickers;

    /// <summary>The "no target" entry of <see cref="TargetOptions"/>.</summary>
    public static TargetPreset NoTarget { get; } = new("", "None", null, null);

    private IReadOnlyList<TargetPreset> _targetOptions = [NoTarget];
    /// <summary>Choices for the editor's target picker: <see cref="NoTarget"/>, then the profile's presets.</summary>
    public IReadOnlyList<TargetPreset> TargetOptions
    {
        get => _targetOptions;
        private set => this.RaiseAndSetIfChanged(ref _targetOptions, value);
    }

    /// <summary>State picker value for <see cref="Selected"/>; options come from the card's <see cref="IdeaCardViewModel.StateOptions"/>.</summary>
    public IdeaState? SelectedState
    {
        get => Selected?.State;
        set
        {
            if (_updatingEditorPickers || value is not { } state || Selected is not { } card)
                return;
            UpdateEditorPickers(() => card.State = state);
        }
    }

    /// <summary>
    /// Target picker value for <see cref="Selected"/>: <see cref="NoTarget"/> when none is set or the key
    /// isn't one of this profile's presets.
    /// </summary>
    public TargetPreset? SelectedTarget
    {
        get => Selected is not { } card
            ? null
            : TargetOptions.FirstOrDefault(p => p.Key == (card.TargetPreset ?? "")) ?? NoTarget;
        set
        {
            if (_updatingEditorPickers || value is null || Selected is not { } card)
                return;
            UpdateEditorPickers(() => card.TargetPreset = value.Key);
        }
    }

    private void UpdateEditorPickers(Action change)
    {
        var outer = _updatingEditorPickers;
        _updatingEditorPickers = true;
        try
        {
            change();
            this.RaisePropertyChanged(nameof(SelectedState));
            this.RaisePropertyChanged(nameof(SelectedTarget));
        }
        finally
        {
            _updatingEditorPickers = outer;
        }
    }

    private bool _isReadOnly;
    /// <summary>True after ideas.json failed to load; nothing is written until a load succeeds.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
    }

    private bool _isPromoting;
    /// <summary>
    /// True from the start of a promote until the workspace and ideas are reloaded; ideas can't be
    /// edited, created or deleted meanwhile.
    /// </summary>
    public bool IsPromoting
    {
        get => _isPromoting;
        private set => this.RaiseAndSetIfChanged(ref _isPromoting, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _canEdit;
    /// <summary>True when ideas can be edited: not read-only and no promote in flight.</summary>
    public bool CanEdit => _canEdit.Value;

    /// <summary>Completes when no promote is in flight.</summary>
    public Task PromoteCompletion => _promoteCompletion;

    /// <summary>Fires with the story's path once a promoted idea's new story is selected in the workspace.</summary>
    public IObservable<string> StoryOpened => _storyOpened.AsObservable();

    private readonly ObservableAsPropertyHelper<bool> _canPromote;
    public bool CanPromote => _canPromote.Value;

    private readonly ObservableAsPropertyHelper<bool> _showsPromote;
    /// <summary>True for anthologies, where an idea can become a new story; short stories have one story.</summary>
    public bool ShowsPromote => _showsPromote.Value;

    /// <summary>States the editor offers. <see cref="IdeaState.Used"/> is set only by promoting.</summary>
    public IReadOnlyList<IdeaState> States => IdeaCardViewModel.SelectableStates;

    /// <summary>Target presets of the current profile, for the editor's target picker.</summary>
    public IReadOnlyList<TargetPreset> TargetPresets => _workspace.Profile.TargetPresets;

    /// <summary>Asks the author to confirm a deletion; defaults to "no".</summary>
    public Func<string, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);

    /// <summary>Asks for the promoted story's file name given a suggestion; null cancels.</summary>
    public Func<string, Task<string?>> AskFileNameAsync { get; set; } = _ => Task.FromResult<string?>(null);

    public ReactiveCommand<(string Title, string Premise), Unit> NewIdeaCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> PromoteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearTagFilterCommand { get; }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes pending edits, then (re)loads ideas for the open workspace. Clears the list when no
    /// workspace is open or the profile has no ideas; a failed load makes the view read-only.
    /// </summary>
    public Task LoadAsync()
    {
        var load = LoadCoreAsync(++_loadGeneration);
        _currentLoad = load;
        return load;
    }

    private async Task LoadCoreAsync(int generation)
    {
        while (true)
        {
            await FlushAsync();
            if (_disposed)
                return;
            if (generation != _loadGeneration)
            {
                await _currentLoad;
                return;
            }

            var root = _workspace.WorkspaceRoot;
            var profile = _workspace.Profile;
            this.RaisePropertyChanged(nameof(TargetPresets));

            // Edits made during the flush belong to the loaded cards; write them before replacing.
            if (_savePending)
                continue;

            if (!_workspace.HasWorkspace || string.IsNullOrEmpty(root) || !profile.ShowsIdeas)
            {
                ReplaceIdeas(null, null, [], profile.TargetPresets);
                IsReadOnly = false;
                return;
            }

            var result = await _ideas.LoadAsync(root);
            if (_disposed)
                return;
            if (generation != _loadGeneration)
            {
                await _currentLoad;
                return;
            }

            // The workspace or project kind changed while reading: load the current one instead.
            if (root != _workspace.WorkspaceRoot || profile.Kind != _workspace.Profile.Kind)
                continue;

            // Edits made while reading are flushed (to the root they belong to) and the file re-read,
            // so replacing the cards never drops them. Nothing below awaits, so none can slip in after.
            if (_savePending)
                continue;

            if (!result.IsSuccess)
            {
                ReplaceIdeas(null, null, [], profile.TargetPresets);
                IsReadOnly = true;
                _notifications.ShowInfo(ReadOnlyNotice);
                return;
            }

            IsReadOnly = false;
            ReplaceIdeas(root, profile.Kind, result.Value!, profile.TargetPresets);
            return;
        }
    }

    /// <summary>
    /// Reloads unless the same workspace and project kind are already loaded and no load is in
    /// flight (an in-flight load may be for a workspace that is no longer open).
    /// </summary>
    private async Task ReloadIfWorkspaceChangedAsync()
    {
        if (_currentLoad.IsCompleted
            && _loadedRoot != null
            && _loadedRoot == _workspace.WorkspaceRoot
            && _loadedKind == _workspace.Profile.Kind)
            return;

        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Failed to load ideas: {ex.Message}");
        }
    }

    /// <summary>True when the loaded ideas belong to the workspace that is open now.</summary>
    private bool ShowsOpenWorkspace => _loadedRoot != null && _loadedRoot == _workspace.WorkspaceRoot;

    private void ReplaceIdeas(
        string? root, ProjectKind? kind, IReadOnlyList<StoryIdea> ideas, IReadOnlyList<TargetPreset> presets)
    {
        var sameRoot = root != null && root == _loadedRoot;
        var selectedId = sameRoot ? Selected?.Id : null;

        // Callers only get here synchronously after checking no save is pending, so no edit
        // to the outgoing cards can be lost.
        _loadedRoot = root;
        _loadedKind = kind;
        _loadedRootChanged.OnNext(Unit.Default);
        Selected = null;
        if (!TargetOptions.Skip(1).SequenceEqual(presets))
            TargetOptions = [NoTarget, .. presets];
        if (!sameRoot)
            TagFilter = null;
        foreach (var card in _allCards)
            RemoveCardSubscriptions(card);
        _allCards.Clear();

        foreach (var idea in ideas)
            AddCard(new IdeaCardViewModel(idea, presets));

        RefreshCards();
        // A card the filters now hide (e.g. a just-promoted, now used idea) is not reselected.
        if (selectedId != null)
            Selected = _cards.FirstOrDefault(c => c.Id == selectedId);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task NewIdeaAsync(string title, string premise)
    {
        // Quick capture can arrive while the open workspace's ideas are still loading (or a promote
        // is reloading them); add the idea to the reloaded list, not the one being replaced.
        await _promoteCompletion;
        await _currentLoad;
        if (IsReadOnly || IsPromoting || _loadedRoot == null)
            return;

        title = title?.Trim() ?? "";
        if (title.Length == 0)
        {
            _notifications.ShowError("An idea needs a title.");
            return;
        }

        var card = new IdeaCardViewModel(
            StoryIdea.Create(title, premise?.Trim() ?? "", DateOnly.FromDateTime(DateTime.Today)),
            _workspace.Profile.TargetPresets);
        AddCard(card);

        // A tag filter would hide the new (untagged) idea.
        TagFilter = null;
        RefreshCards();

        var save = SaveNowAsync();
        Selected = card;
        await save;
    }

    public async Task DeleteSelectedAsync()
    {
        var card = Selected;
        if (card == null || IsReadOnly || IsPromoting || _loadedRoot == null)
            return;

        var title = string.IsNullOrWhiteSpace(card.Title) ? "this idea" : $"\"{card.Title}\"";
        if (!await ConfirmAsync($"Delete {title}? This can't be undone."))
            return;

        RemoveCardSubscriptions(card);
        _allCards.Remove(card);
        var save = SaveNowAsync();
        Selected = null;
        RefreshCards();
        await save;
    }

    public async Task PromoteSelectedAsync()
    {
        var card = Selected;
        if (card == null || card.StoryId != null || !CanPromote || !ShowsOpenWorkspace)
            return;
        var root = _loadedRoot!;

        // The promoter re-reads the idea from disk, so pending edits must land first.
        if (!await TryFlushPendingEditsBeforePromoteAsync())
            return;

        var fileName = await AskFileNameAsync(StoryIdeaPromoter.SuggestFileName(card.Title));
        if (string.IsNullOrWhiteSpace(fileName) || root != _loadedRoot || !ShowsOpenWorkspace || IsPromoting)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _promoteCompletion = completion.Task;
        IsPromoting = true;
        var promoterRan = false;
        try
        {
            // Edits made while the file name was asked for; nothing more is saved until the reload.
            if (!await TryFlushPendingEditsBeforePromoteAsync())
                return;

            using var suppressFileWatcher = _workspace.SuppressFileWatcher();
            promoterRan = true;
            var result = await _promoter.PromoteAsync(
                root, _workspace.BookTxtPath, card.ToIdea(), fileName.Trim(), _workspace.Profile,
                _workspace.MetadataStore);

            // Disposed mid-promote (window closed): the story exists on disk; there is no view left to update.
            if (_disposed)
                return;

            if (!result.IsSuccess)
                _notifications.ShowError(result.Error!);

            // Another workspace opened meanwhile: its sidebar and editor aren't this story's.
            if (root == _workspace.WorkspaceRoot && result.FileCreated)
            {
                // Even when setup failed after creating the file, the sidebar must list the new story.
                var reload = await _workspace.ReloadProjectAsync();
                if (!reload.IsSuccess)
                    _notifications.ShowError(reload.Error!);
                else if (result.IsSuccess && !_disposed && _workspace.SelectChapterByPath(result.Value!))
                    _storyOpened.OnNext(result.Value!);
            }
        }
        finally
        {
            try
            {
                if (promoterRan && !_disposed)
                    await LoadAsync();
            }
            finally
            {
                IsPromoting = false;
                completion.SetResult();
            }
        }
    }

    private async Task<bool> TryFlushPendingEditsBeforePromoteAsync()
    {
        if (await TryFlushAsync())
            return true;
        _notifications.ShowError("The idea wasn't promoted because its latest edits couldn't be saved.");
        return false;
    }

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>Writes pending edits now and waits for every queued save to finish.</summary>
    public Task FlushAsync()
    {
        if (!_savePending)
            return _saveChain;
        return SaveNowAsync();
    }

    private Task SaveNowAsync()
    {
        _savePending = false;
        if (IsReadOnly || _loadedRoot == null)
            return _saveChain;

        var root = _loadedRoot;
        var snapshot = _allCards.Select(c => c.ToIdea()).ToList();
        _saveChain = SaveAfterAsync(_saveChain, root, snapshot);
        return _saveChain;
    }

    /// <summary>
    /// Writes pending edits now; true when nothing is left unsaved (the latest write succeeded).
    /// </summary>
    public async Task<bool> TryFlushAsync()
    {
        await FlushAsync();
        return !_savePending && !_lastSaveFailed;
    }

    private async Task SaveAfterAsync(Task previous, string root, IReadOnlyList<StoryIdea> snapshot)
    {
        await previous;
        try
        {
            var result = await _ideas.SaveAsync(root, snapshot);
            _lastSaveFailed = !result.IsSuccess;
            if (!result.IsSuccess)
                _notifications.ShowError(result.Error!);
        }
        catch (Exception ex)
        {
            _lastSaveFailed = true;
            _notifications.ShowError($"Failed to save ideas: {ex.Message}");
        }
    }

    private void OnCardEdited()
    {
        // While promoting, the cards are about to be replaced by the reloaded file; saving them
        // would write back a stale copy of the promoted idea.
        if (_disposed || IsReadOnly || IsPromoting || _loadedRoot == null)
            return;
        _savePending = true;
        _saveRequests.OnNext(Unit.Default);
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    private void AddCard(IdeaCardViewModel card)
    {
        _allCards.Add(card);
        _cardSubscriptions[card] = new System.Reactive.Disposables.CompositeDisposable(
            card.Edited.Subscribe(_ => OnCardEdited()),
            card.WhenAnyValue(c => c.State, c => c.Tags).Skip(1).Subscribe(_ => RefreshCards()));
    }

    private void RemoveCardSubscriptions(IdeaCardViewModel card)
    {
        if (_cardSubscriptions.Remove(card, out var subscription))
            subscription.Dispose();
        card.Dispose();
    }

    private void RefreshCards()
    {
        var tag = string.IsNullOrWhiteSpace(TagFilter) ? null : TagFilter.Trim();
        var visible = _allCards
            .Where(c => ShowUsedAndShelved || c.State is IdeaState.New or IdeaState.Developing)
            .Where(c => tag == null || c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (!visible.SequenceEqual(_cards))
            _cards.Load(visible);

        var tags = StoryIdeasService.NormalizeTags(_allCards.SelectMany(c => c.Tags))
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!tags.SequenceEqual(AllTags, StringComparer.Ordinal))
            AllTags = tags;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disposables.Dispose();
        foreach (var card in _allCards)
            RemoveCardSubscriptions(card);
        _allCards.Clear();
        _saveRequests.OnCompleted();
        _saveRequests.Dispose();
        _loadedRootChanged.OnCompleted();
        _loadedRootChanged.Dispose();
        _storyOpened.OnCompleted();
        _storyOpened.Dispose();
    }
}
