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
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

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

    /// <summary>Workspace root the current ideas were loaded from; null when nothing is loaded.</summary>
    private string? _loadedRoot;
    private bool _savePending;
    private Task _saveChain = Task.CompletedTask;
    private int _loadGeneration;

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

        _canPromote = Observable.CombineLatest(
                profile, selectedState, this.WhenAnyValue(x => x.IsReadOnly),
                (p, state, readOnly) => p.Kind == ProjectKind.Anthology && state is { } s && s != IdeaState.Used && !readOnly)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.CanPromote);
        Disposables.Add(_canPromote);

        var canCreate = Observable.CombineLatest(
            profile, this.WhenAnyValue(x => x.IsReadOnly),
            (p, readOnly) => p.ShowsIdeas && !readOnly);
        var canDelete = Observable.CombineLatest(
            this.WhenAnyValue(x => x.Selected), this.WhenAnyValue(x => x.IsReadOnly),
            (selected, readOnly) => selected != null && !readOnly);

        NewIdeaCommand = ReactiveCommand.CreateFromTask<(string Title, string Premise)>(
            request => NewIdeaAsync(request.Title, request.Premise), canCreate);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, canDelete);
        PromoteSelectedCommand = ReactiveCommand.CreateFromTask(
            PromoteSelectedAsync, this.WhenAnyValue(x => x.CanPromote));

        foreach (var command in new IHandleObservableErrors[] { NewIdeaCommand, DeleteSelectedCommand, PromoteSelectedCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications.ShowError(ex.Message)));

        Disposables.Add(
            _saveRequests
                .Throttle(SaveDelay, saveScheduler ?? AvaloniaScheduler.Instance)
                .Subscribe(_ => FlushAsync()));

        // Moving to another card writes the previous card's pending edits right away.
        Disposables.Add(
            this.WhenAnyValue(x => x.Selected)
                .Skip(1)
                .Subscribe(_ => FlushAsync()));

        Disposables.Add(
            this.WhenAnyValue(x => x.TagFilter, x => x.ShowUsedAndShelved)
                .Skip(1)
                .Subscribe(_ => RefreshCards()));

        Disposables.Add(
            Observable.Merge(workspace.WorkspaceChanged.Select(_ => Unit.Default), profile.Select(_ => Unit.Default))
                .Subscribe(_ => ReloadInBackgroundAsync()));
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
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    private bool _isReadOnly;
    /// <summary>True after ideas.json failed to load; nothing is written until a load succeeds.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _canPromote;
    public bool CanPromote => _canPromote.Value;

    public IReadOnlyList<IdeaState> States { get; } = Enum.GetValues<IdeaState>();

    /// <summary>Target presets of the current profile, for the editor's target picker.</summary>
    public IReadOnlyList<TargetPreset> TargetPresets => _workspace.Profile.TargetPresets;

    /// <summary>Asks the author to confirm a deletion; defaults to "no".</summary>
    public Func<string, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);

    /// <summary>Asks for the promoted story's file name given a suggestion; null cancels.</summary>
    public Func<string, Task<string?>> AskFileNameAsync { get; set; } = _ => Task.FromResult<string?>(null);

    public ReactiveCommand<(string Title, string Premise), Unit> NewIdeaCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> PromoteSelectedCommand { get; }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes pending edits, then (re)loads ideas for the open workspace. Clears the list when no
    /// workspace is open or the profile has no ideas; a failed load makes the view read-only.
    /// </summary>
    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        await FlushAsync();
        if (generation != _loadGeneration)
            return;

        var root = _workspace.WorkspaceRoot;
        var profile = _workspace.Profile;
        this.RaisePropertyChanged(nameof(TargetPresets));

        if (!_workspace.HasWorkspace || string.IsNullOrEmpty(root) || !profile.ShowsIdeas)
        {
            ReplaceIdeas(null, [], profile.TargetPresets);
            IsReadOnly = false;
            return;
        }

        var result = await _ideas.LoadAsync(root);
        if (generation != _loadGeneration)
            return;

        if (!result.IsSuccess)
        {
            ReplaceIdeas(null, [], profile.TargetPresets);
            IsReadOnly = true;
            _notifications.ShowInfo(ReadOnlyNotice);
            return;
        }

        IsReadOnly = false;
        ReplaceIdeas(root, result.Value!, profile.TargetPresets);
    }

    private async Task ReloadInBackgroundAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Failed to load ideas: {ex.Message}");
        }
    }

    private void ReplaceIdeas(string? root, IReadOnlyList<StoryIdea> ideas, IReadOnlyList<TargetPreset> presets)
    {
        var selectedId = root != null && root == _loadedRoot ? Selected?.Id : null;

        _savePending = false;
        _loadedRoot = root;
        Selected = null;
        foreach (var card in _allCards)
            RemoveCardSubscriptions(card);
        _allCards.Clear();

        foreach (var idea in ideas)
            AddCard(new IdeaCardViewModel(idea, presets));

        RefreshCards();
        if (selectedId != null)
            Selected = _allCards.FirstOrDefault(c => c.Id == selectedId);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task NewIdeaAsync(string title, string premise)
    {
        if (IsReadOnly || _loadedRoot == null)
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
        if (card == null || IsReadOnly || _loadedRoot == null)
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
        if (card == null || !CanPromote)
            return;

        // The promoter re-reads the idea from disk, so pending edits must land first.
        await FlushAsync();

        var fileName = await AskFileNameAsync(StoryIdeaPromoter.SuggestFileName(card.Title));
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        using (_workspace.SuppressFileWatcher())
        {
            var result = await _promoter.PromoteAsync(
                _workspace.WorkspaceRoot, _workspace.BookTxtPath, card.ToIdea(), fileName.Trim(), _workspace.Profile);

            if (result.IsSuccess)
            {
                var reload = await _workspace.ReloadProjectAsync();
                if (reload.IsSuccess)
                    _workspace.SelectChapterByPath(result.Value!);
                else
                    _notifications.ShowError(reload.Error!);
            }
            else
            {
                _notifications.ShowError(result.Error!);
            }
        }

        await LoadAsync();
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

    private async Task SaveAfterAsync(Task previous, string root, IReadOnlyList<StoryIdea> snapshot)
    {
        await previous;
        try
        {
            var result = await _ideas.SaveAsync(root, snapshot);
            if (!result.IsSuccess)
                _notifications.ShowError(result.Error!);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Failed to save ideas: {ex.Message}");
        }
    }

    private void OnCardEdited()
    {
        if (IsReadOnly || _loadedRoot == null)
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
    }
}
