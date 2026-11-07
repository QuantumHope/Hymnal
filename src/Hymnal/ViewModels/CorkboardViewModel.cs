using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using DynamicData.Binding;
using Hymnal.Core.Common;
using Unit = Hymnal.Core.Common.Unit;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

public sealed record CorkboardStructuralError(string Operation, string? Path, string Message, string? BookTxtPath = null);

public sealed record ReorderCardRequest(
    string RelativePath,
    int? NewIndex = null,
    string? AfterRelativePath = null,
    string? BeforeRelativePath = null);

public sealed record CorkboardDropRequest(
    string RelativePath,
    string? TargetPartPath = null,
    string? AfterRelativePath = null,
    string? BeforeRelativePath = null);

public sealed record RenameCardRequest(string ExistingPath, string ReplacementPath);

public sealed record RenameChapterRequest(string ExistingPath, string NewTitle);

public sealed record RenamePartRequest(string ExistingPath, string NewTitle);

public sealed record CreateChapterRequest(string ChapterPath, string Content, int Index);

public sealed record CreatePartRequest(string PartPath, string Title, int Index);

public sealed record IncludeExistingChapterRequest(string ChapterPath, int? Index = null, string? PartPath = null);

public sealed record RemoveChapterRequest(string ChapterPath);

public sealed record DeleteChapterRequest(string ChapterPath, bool Confirmed);

/// <summary>
/// Coordinates the plan-mode corkboard projection, selection state, open requests,
/// and all structural Book.txt edits.
/// </summary>
public sealed class CorkboardViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly IBookTxtStructureService _structureService;
    private readonly IOrphanFileDiscoveryService _orphanDiscovery;
    private readonly IAppSettingsStore _settingsStore;
    private readonly INotificationService _notificationService;
    private readonly ManuscriptService _manuscriptService;
    private IReadOnlyList<OrphanFileInfo> _orphanFiles = Array.Empty<OrphanFileInfo>();
    private int _rebuildGeneration;
    private readonly Subject<ChapterViewModel> _openChapterRequested = new();
    private readonly Subject<ChapterViewModel> _beatsRequested = new();
    private readonly ObservableCollectionExtended<CorkboardItemViewModel> _items = new();
    private readonly Dictionary<string, bool> _partExpandedState = new(StringComparer.OrdinalIgnoreCase);
    private bool _isViewActive = true;
    private bool _needsRebuild;
    private string? _pendingSelectionPath;
    private InlineCreateItemViewModel? _activeInlineCreate;

    public ReadOnlyObservableCollection<CorkboardItemViewModel> Items { get; }

    public bool HasItems => _items.Count > 0;

    public IObservable<ChapterViewModel> OpenChapterRequested => _openChapterRequested.AsObservable();

    /// <summary>
    /// Anthology only: the author asked for a story's beat board. Carries the story's node — a
    /// single-file story's chapter, or the Part of a story told in several scenes.
    /// </summary>
    public IObservable<ChapterViewModel> BeatsRequested => _beatsRequested.AsObservable();

    private CardViewModel? _selectedCard;
    public CardViewModel? SelectedCard
    {
        get => _selectedCard;
        private set => this.RaiseAndSetIfChanged(ref _selectedCard, value);
    }

    private CorkboardStructuralError? _lastStructuralError;
    public CorkboardStructuralError? LastStructuralError
    {
        get => _lastStructuralError;
        private set => this.RaiseAndSetIfChanged(ref _lastStructuralError, value);
    }

    private CardDisplaySize _cardDisplaySize = CardDisplaySize.Large;
    public CardDisplaySize CardDisplaySize
    {
        get => _cardDisplaySize;
        private set => this.RaiseAndSetIfChanged(ref _cardDisplaySize, value);
    }

    public ReactiveCommand<CorkboardItemViewModel?, System.Reactive.Unit> SelectCardCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenSelectedCardCommand { get; }
    /// <summary>
    /// Double-click: opens the card in the editor, except in an anthology, where a story card or a
    /// Part divider opens that story's beats instead (scene cards inside a Part still open the editor).
    /// </summary>
    public ReactiveCommand<CorkboardItemViewModel?, System.Reactive.Unit> OpenCardCommand { get; }
    /// <summary>Opens a chapter card in the editor, whatever the project kind.</summary>
    public ReactiveCommand<CorkboardItemViewModel?, System.Reactive.Unit> OpenInEditorCommand { get; }
    /// <summary>Anthology only: opens the beats of the story a card or Part divider belongs to.</summary>
    public ReactiveCommand<CorkboardItemViewModel?, System.Reactive.Unit> ShowBeatsCommand { get; }
    public ReactiveCommand<ReorderCardRequest, System.Reactive.Unit> ReorderCardCommand { get; }
    public ReactiveCommand<CorkboardDropRequest, System.Reactive.Unit> DropCardCommand { get; }
    public ReactiveCommand<RenameCardRequest, System.Reactive.Unit> RenameCardCommand { get; }
    public ReactiveCommand<CreateChapterRequest, System.Reactive.Unit> CreateChapterCommand { get; }
    public ReactiveCommand<CreatePartRequest, System.Reactive.Unit> CreatePartCommand { get; }
    public ReactiveCommand<IncludeExistingChapterRequest, System.Reactive.Unit> IncludeExistingChapterCommand { get; }
    public ReactiveCommand<RemoveChapterRequest, System.Reactive.Unit> RemoveFromBookCommand { get; }
    public ReactiveCommand<DeleteChapterRequest, System.Reactive.Unit> DeleteChapterCommand { get; }
    public ReactiveCommand<CardDisplaySize, System.Reactive.Unit> SetCardSizeCommand { get; }
    public ReactiveCommand<PartDividerItemViewModel, System.Reactive.Unit> TogglePartExpandedCommand { get; }

    private bool _showExcludedFiles = true;
    /// <summary>When true, orphan files on disk are shown as excluded cards on the plan board.</summary>
    public bool ShowExcludedFiles
    {
        get => _showExcludedFiles;
        set
        {
            if (_showExcludedFiles == value)
                return;

            this.RaiseAndSetIfChanged(ref _showExcludedFiles, value);
            _ = PersistShowExcludedFilesAsync(value);
            ApplyProjection(_orphanFiles);
        }
    }

    public CorkboardViewModel(
        WorkspaceViewModel workspace,
        IBookTxtStructureService structureService,
        IOrphanFileDiscoveryService orphanDiscovery,
        IAppSettingsStore settingsStore,
        INotificationService notificationService,
        ManuscriptService manuscriptService)
    {
        _workspace = workspace;
        _structureService = structureService;
        _orphanDiscovery = orphanDiscovery;
        _settingsStore = settingsStore;
        _notificationService = notificationService;
        _manuscriptService = manuscriptService;

        try
        {
            _showExcludedFiles = _settingsStore.GetAsync<bool?>("showExcludedFiles").GetAwaiter().GetResult() ?? true;
        }
        catch
        {
            _showExcludedFiles = true;
        }

        Items = new ReadOnlyObservableCollection<CorkboardItemViewModel>(_items);

        var nodeChanges = (INotifyCollectionChanged)_workspace.Nodes;
        Disposables.Add(
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    handler => nodeChanges.CollectionChanged += handler,
                    handler => nodeChanges.CollectionChanged -= handler)
                .Throttle(TimeSpan.FromMilliseconds(50), TaskPoolScheduler.Default)
                .Subscribe(_ => RequestRebuild()));

        Disposables.Add(
            _workspace.WorkspaceChanged.Subscribe(_ => RequestRebuild()));

        Disposables.Add(
            _workspace.WhenAnyValue(x => x.Profile)
                .Skip(1)
                .Subscribe(_ =>
                {
                    this.RaisePropertyChanged(nameof(Profile));
                    this.RaisePropertyChanged(nameof(IsAnthology));
                }));

        Disposables.Add(_openChapterRequested);
        Disposables.Add(_beatsRequested);

        SelectCardCommand = ReactiveCommand.CreateFromTask<CorkboardItemViewModel?>(SelectCardAsync);
        OpenSelectedCardCommand = ReactiveCommand.CreateFromTask(OpenSelectedCardAsync,
            this.WhenAnyValue(x => x.SelectedCard).Select(card => card != null));
        OpenCardCommand = ReactiveCommand.CreateFromTask<CorkboardItemViewModel?>(OpenCardAsync);
        OpenInEditorCommand = ReactiveCommand.CreateFromTask<CorkboardItemViewModel?>(OpenInEditorAsync);
        ShowBeatsCommand = ReactiveCommand.CreateFromTask<CorkboardItemViewModel?>(ShowBeatsAsync);
        ReorderCardCommand = ReactiveCommand.CreateFromTask<ReorderCardRequest>(ReorderCardAsync);
        DropCardCommand = ReactiveCommand.CreateFromTask<CorkboardDropRequest>(DropCardAsync);
        RenameCardCommand = ReactiveCommand.CreateFromTask<RenameCardRequest>(RenameCardAsync);
        CreateChapterCommand = ReactiveCommand.CreateFromTask<CreateChapterRequest>(CreateChapterAsync);
        CreatePartCommand = ReactiveCommand.CreateFromTask<CreatePartRequest>(CreatePartAsync);
        IncludeExistingChapterCommand = ReactiveCommand.CreateFromTask<IncludeExistingChapterRequest>(IncludeExistingChapterAsync);
        RemoveFromBookCommand = ReactiveCommand.CreateFromTask<RemoveChapterRequest>(RemoveFromBookAsync);
        DeleteChapterCommand = ReactiveCommand.CreateFromTask<DeleteChapterRequest>(DeleteChapterAsync);
        SetCardSizeCommand = ReactiveCommand.Create<CardDisplaySize>(size => CardDisplaySize = size);
        TogglePartExpandedCommand = ReactiveCommand.Create<PartDividerItemViewModel>(TogglePartExpanded);

        Disposables.Add(SelectCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(OpenSelectedCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(OpenCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(OpenInEditorCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(ShowBeatsCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(ReorderCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(DropCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(RenameCardCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(CreateChapterCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(CreatePartCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(IncludeExistingChapterCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(RemoveFromBookCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(DeleteChapterCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(SetCardSizeCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));
        Disposables.Add(TogglePartExpandedCommand.ThrownExceptions.Subscribe(ReportUnexpectedError));

        ApplyProjection(_orphanFiles);
        _ = DiscoverOrphansAndRebuildAsync();
    }

    /// <summary>Called when Plan mode becomes active or inactive.</summary>
    public void SetViewActive(bool active)
    {
        _isViewActive = active;
        if (active && _needsRebuild)
        {
            _needsRebuild = false;
            ApplyProjection(_orphanFiles);
            _ = DiscoverOrphansAndRebuildAsync();
        }
    }

    private void RequestRebuild()
    {
        if (!_isViewActive)
        {
            _needsRebuild = true;
            return;
        }

        ApplyProjection(_orphanFiles);
        _ = DiscoverOrphansAndRebuildAsync();
    }

    /// <summary>Profile of the open workspace; supplies UI nouns for board menus.</summary>
    public ProjectProfile Profile => _workspace.Profile;

    /// <summary>True in an anthology, where cards offer Beats and Open actions.</summary>
    public bool IsAnthology => _workspace.Profile.Kind == ProjectKind.Anthology;

    public Task<string?> PickManuscriptFileAsync() => _workspace.PickManuscriptFileAsync();

    public string ToManuscriptRelativePath(string absolutePath) => _workspace.ToManuscriptRelativePath(absolutePath);

    public int GetBookInsertIndex() => _workspace.Nodes.Count;

    /// <summary>
    /// Returns the Book.txt insert index for a new book-level chapter: the position of the
    /// first Part node, so the chapter lands before any parts. Falls back to
    /// <see cref="GetBookInsertIndex"/> when the book contains no parts.
    /// </summary>
    public int GetBookChapterInsertIndex()
    {
        var nodes = _workspace.Nodes.ToList();
        var firstPartIndex = nodes.FindIndex(n => n.Node.Kind == NodeKind.Part);
        return firstPartIndex >= 0 ? firstPartIndex : nodes.Count;
    }

    /// <summary>
    /// Returns the Book.txt insert index immediately after the specified chapter node
    /// (all entries: parts + chapters). Used for inline chapter creation via context menu.
    /// </summary>
    public int GetInsertIndexAfterChapter(string chapterRelativePath)
    {
        var nodes = _workspace.Nodes.ToList();
        var index = nodes.FindIndex(n =>
            string.Equals(n.Node.RelativePath, chapterRelativePath, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index + 1 : GetBookInsertIndex();
    }

    public int GetInsertIndexAfterPart(string partRelativePath)
    {
        var nodes = _workspace.Nodes.ToList();
        var partIndex = nodes.FindIndex(node =>
            node.Node.Kind == NodeKind.Part &&
            string.Equals(node.Node.RelativePath, partRelativePath, StringComparison.OrdinalIgnoreCase));

        if (partIndex < 0)
            return GetBookInsertIndex();

        var insertIndex = partIndex + 1;
        for (var i = partIndex + 1; i < nodes.Count; i++)
        {
            if (nodes[i].Node.Kind == NodeKind.Part)
                break;

            insertIndex++;
        }

        return insertIndex;
    }

    // ── Inline chapter creation ──────────────────────────────────────────────

    /// <summary>
    /// Inserts a temporary <see cref="InlineCreateItemViewModel"/> into the board at the
    /// correct visual position for the given Book.txt insert index.
    /// Call <see cref="CommitInlineCreateAsync"/> or <see cref="CancelInlineCreate"/> to finish.
    /// </summary>
    public void BeginInlineCreate(int bookTxtInsertIndex, PartDividerItemViewModel? part)
    {
        CancelInlineCreate();

        var item = new InlineCreateItemViewModel(bookTxtInsertIndex, part?.RelativePath, part);
        _activeInlineCreate = item;
        InsertInlineCreateAtIndex(item, bookTxtInsertIndex);
    }

    private void InsertInlineCreateAtIndex(InlineCreateItemViewModel item, int bookTxtInsertIndex)
    {
        var nodes = _workspace.Nodes.ToList();
        var insertAtItemsIndex = ResolveInlineCreateItemsIndex(nodes, bookTxtInsertIndex);
        _items.Insert(insertAtItemsIndex, item);
    }

    private int ResolveInlineCreateItemsIndex(IReadOnlyList<ChapterViewModel> nodes, int bookTxtInsertIndex)
    {
        if (_items.Count == 0)
            return 0;

        if (bookTxtInsertIndex <= 0)
            return 0;

        if (nodes.Count == 0)
            return _items.Count;

        var priorNodeIndex = Math.Min(bookTxtInsertIndex - 1, nodes.Count - 1);
        var priorNode = nodes[priorNodeIndex].Node;

        if (priorNode.Kind == NodeKind.Part)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind == CorkboardItemKind.PartDivider
                    && string.Equals(_items[i].RelativePath, priorNode.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1;
                }
            }

            return _items.Count;
        }

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Kind is not CorkboardItemKind.ChapterCard and not CorkboardItemKind.ExcludedChapterCard)
                continue;

            if (string.Equals(_items[i].RelativePath, priorNode.RelativePath, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return _items.Count;
    }

    /// <summary>Removes the active inline create card without creating a chapter.</summary>
    public void CancelInlineCreate()
    {
        if (_activeInlineCreate == null)
            return;

        _items.Remove(_activeInlineCreate);
        _activeInlineCreate.Dispose();
        _activeInlineCreate = null;
    }

    /// <summary>
    /// Commits inline creation: generates a slug-based filename from <paramref name="title"/>,
    /// creates the chapter file and Book.txt entry, then reloads the workspace.
    /// Cancels the inline item first so the projection doesn't see it during reload.
    /// </summary>
    public async Task CommitInlineCreateAsync(string title)
    {
        if (_activeInlineCreate == null)
            return;

        if (string.IsNullOrWhiteSpace(title))
        {
            CancelInlineCreate();
            return;
        }

        var insertIndex = _activeInlineCreate.BookTxtInsertIndex;
        var partPath = _activeInlineCreate.PartPath;

        CancelInlineCreate();

        var partFolder = GetPartFolderPrefix(partPath);
        var slug = TitleToSlug(title.Trim());
        var filePath = string.IsNullOrEmpty(partFolder) ? $"{slug}.md" : $"{partFolder}/{slug}.md";
        var content = $"# {title.Trim()}\n\n";

        await ExecuteStructuralOperationAsync(
            "Create chapter",
            filePath,
            () => _structureService.CreateNewChapterAsync(_workspace.BookTxtPath, filePath, content, insertIndex));
    }

    private static string TitleToSlug(string title)
    {
        var lower = title.ToLowerInvariant();
        var chars = lower.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "new-chapter" : slug;
    }

    private static string? GetPartFolderPrefix(string? partPath)
    {
        if (string.IsNullOrWhiteSpace(partPath))
            return null;

        var normalized = partPath.Replace('\\', '/').Trim('/');
        var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    private void TogglePartExpanded(PartDividerItemViewModel part)
    {
        var path = part.RelativePath;
        var nextExpanded = !_partExpandedState.GetValueOrDefault(path, part.IsExpanded);
        _partExpandedState[path] = nextExpanded;
        part.IsExpanded = nextExpanded;
    }

    private async Task DiscoverOrphansAndRebuildAsync()
    {
        var generation = ++_rebuildGeneration;

        IReadOnlyList<OrphanFileInfo> orphans = Array.Empty<OrphanFileInfo>();
        if (_workspace.HasWorkspace && !string.IsNullOrWhiteSpace(_workspace.ManuscriptRoot))
        {
            var entries = _workspace.Nodes
                .Where(node => !node.Node.IsExcluded)
                .Select(node => node.Node.RelativePath)
                .ToList();

            try
            {
                orphans = await _orphanDiscovery
                    .DiscoverAsync(_workspace.ManuscriptRoot, entries)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to discover excluded files: {ex.Message}");
            }
        }

        if (generation != _rebuildGeneration)
            return;

        if (global::Avalonia.Application.Current is not null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _rebuildGeneration)
                    return;

                _orphanFiles = orphans;
                ApplyProjection(_orphanFiles);
            });
        }
        else
        {
            _orphanFiles = orphans;
            ApplyProjection(_orphanFiles);
        }
    }

    private void ApplyProjection(IReadOnlyList<OrphanFileInfo> orphans)
    {
        if (global::Avalonia.Application.Current is not null
            && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyProjection(orphans)).GetAwaiter().GetResult();
            return;
        }

        var selectedPath = _pendingSelectionPath ?? SelectedCard?.RelativePath;

        // Dispose projected items but preserve the transient inline create item so its
        // TitleText (user's in-progress typing) survives the rebuild.
        var itemsToDispose = _items.Where(i => i is not InlineCreateItemViewModel).ToList();
        CorkboardItemViewModel.DisposeItems(itemsToDispose);
        _items.Clear();

        foreach (var item in CorkboardItemViewModel.Project(
                     _workspace.Nodes,
                     _partExpandedState,
                     orphans,
                     ShowExcludedFiles))
        {
            _items.Add(item);
        }

        // Re-insert active inline create at the correct visual position.
        if (_activeInlineCreate != null)
            InsertInlineCreateAtIndex(_activeInlineCreate, _activeInlineCreate.BookTxtInsertIndex);

        this.RaisePropertyChanged(nameof(HasItems));
        RestoreSelection(selectedPath);
    }

    private async Task PersistShowExcludedFilesAsync(bool value)
    {
        try
        {
            await _settingsStore.SetAsync("showExcludedFiles", value).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal preference persistence failure.
        }
    }

    private Task SelectCardAsync(CorkboardItemViewModel? item)
    {
        if (item is not ChapterCardItemViewModel cardItem)
            return Task.CompletedTask;

        SetSelectedCard(cardItem.Card);
        return Task.CompletedTask;
    }

    private Task OpenSelectedCardAsync()
    {
        if (SelectedCard == null)
            return Task.CompletedTask;

        _openChapterRequested.OnNext(SelectedCard.Chapter);
        return Task.CompletedTask;
    }

    private Task OpenCardAsync(CorkboardItemViewModel? item)
    {
        var opensBeats = IsAnthology && item switch
        {
            PartDividerItemViewModel => true,
            ChapterCardItemViewModel card => string.IsNullOrEmpty(card.OwningPartPath),
            _ => false
        };

        return opensBeats ? ShowBeatsAsync(item) : OpenInEditorAsync(item);
    }

    private Task OpenInEditorAsync(CorkboardItemViewModel? item)
    {
        if (item is not ChapterCardItemViewModel cardItem)
            return Task.CompletedTask;

        SetSelectedCard(cardItem.Card);
        _openChapterRequested.OnNext(cardItem.Card.Chapter);
        return Task.CompletedTask;
    }

    private Task ShowBeatsAsync(CorkboardItemViewModel? item)
    {
        if (!IsAnthology)
            return Task.CompletedTask;

        if (item is ChapterCardItemViewModel cardItem)
            SetSelectedCard(cardItem.Card);

        if (FindStory(item) is { } story)
            _beatsRequested.OnNext(story);
        return Task.CompletedTask;
    }

    /// <summary>The story an item belongs to: a Part divider's Part, a scene's Part, or a top-level card itself.</summary>
    private ChapterViewModel? FindStory(CorkboardItemViewModel? item) => item switch
    {
        PartDividerItemViewModel part => part.Part,
        ChapterCardItemViewModel card when string.IsNullOrEmpty(card.OwningPartPath) => card.Card.Chapter,
        ChapterCardItemViewModel card => _workspace.Nodes.FirstOrDefault(node =>
            node.Node.Kind == NodeKind.Part &&
            string.Equals(node.Node.RelativePath, card.OwningPartPath, StringComparison.OrdinalIgnoreCase)),
        _ => null
    };

    private async Task ReorderCardAsync(ReorderCardRequest request)
    {
        if (!TryFindCurrentChapter(request.RelativePath, out _))
        {
            ReportStructuralFailure("Reorder card", request.RelativePath,
                $"Chapter '{request.RelativePath}' is not on the current board.");
            return;
        }

        if (!TryResolveReorderIndex(request, out var newIndex, out var error))
        {
            ReportStructuralFailure("Reorder card", request.RelativePath, error!);
            return;
        }

        if (!_workspace.HasWorkspace || string.IsNullOrWhiteSpace(_workspace.BookTxtPath))
        {
            ReportStructuralFailure("Reorder card", request.RelativePath, "No workspace is open.");
            return;
        }

        var previousSelectionPath = SelectedCard?.RelativePath;

        try
        {
            using var _ = _manuscriptService.SuppressFileWatcher();

            var result = await _structureService.ReorderEntryAsync(
                _workspace.BookTxtPath, request.RelativePath, newIndex);

            if (!result.IsSuccess)
            {
                ReportStructuralFailure("Reorder card", request.RelativePath,
                    result.Error ?? "Structural action failed.");
                return;
            }

            LastStructuralError = null;
            var reorderResult = await _workspace.ReorderNodesAsync();
            if (!reorderResult.IsSuccess)
            {
                ReportStructuralFailure("Reorder card", request.RelativePath,
                    reorderResult.Error ?? "Node reorder failed.");
                return;
            }

            RestoreSelection(previousSelectionPath);
        }
        catch (Exception ex)
        {
            ReportStructuralFailure("Reorder card", request.RelativePath, ex.Message);
        }
    }

    private async Task DropCardAsync(CorkboardDropRequest request)
    {
        if (!_workspace.HasWorkspace || string.IsNullOrWhiteSpace(_workspace.BookTxtPath))
        {
            ReportStructuralFailure("Drop card", request.RelativePath, "No workspace is open.");
            return;
        }

        if (!TryFindCurrentChapter(request.RelativePath, out _))
        {
            var excludedMatch = _items.Any(item =>
                item.Kind == CorkboardItemKind.ExcludedChapterCard &&
                string.Equals(item.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase));

            ReportStructuralFailure(
                "Drop card",
                request.RelativePath,
                excludedMatch
                    ? $"Chapter '{request.RelativePath}' is excluded and cannot be reordered as an included Book.txt entry."
                    : $"Chapter '{request.RelativePath}' is not on the current board.");
            return;
        }

        if (!TryResolveDrop(request, out var operation, out var error))
        {
            ReportStructuralFailure("Drop card", request.RelativePath, error!);
            return;
        }

        try
        {
            using var _ = _manuscriptService.SuppressFileWatcher();

            var result = operation.IsCrossPart
                ? await _structureService.MoveEntryAsync(
                    _workspace.BookTxtPath,
                    request.RelativePath,
                    operation.ReplacementPath!,
                    operation.NewIndex)
                : await _structureService.ReorderEntryAsync(
                    _workspace.BookTxtPath,
                    request.RelativePath,
                    operation.NewIndex);

            if (!result.IsSuccess)
            {
                var failureMessage = result.Error ?? "Structural action failed.";

                if (operation.IsCrossPart && IsCommittedMoveManifestFailure(failureMessage))
                {
                    var recoveryReload = await _workspace.ReloadCurrentWorkspaceAsync();
                    if (recoveryReload.IsSuccess)
                    {
                        RequestRebuild();
                        RestoreSelection(operation.SelectionPath);
                    }
                    else
                    {
                        failureMessage = $"{failureMessage} Workspace reload after committed move also failed: {recoveryReload.Error ?? "Unknown reload failure."}";
                    }
                }

                ReportStructuralFailure("Drop card", request.RelativePath, failureMessage);
                return;
            }

            LastStructuralError = null;
            var reloadResult = await _workspace.ReloadCurrentWorkspaceAsync();
            if (!reloadResult.IsSuccess)
            {
                ReportStructuralFailure("Drop card", request.RelativePath,
                    reloadResult.Error ?? "Workspace reload failed.");
                return;
            }

            RequestRebuild();
            RestoreSelection(operation.SelectionPath);
        }
        catch (Exception ex)
        {
            ReportStructuralFailure("Drop card", request.RelativePath, ex.Message);
        }
    }

    private async Task RenameCardAsync(RenameCardRequest request)
    {
        if (!TryFindCurrentChapter(request.ExistingPath, out _))
        {
            ReportStructuralFailure("Rename card", request.ExistingPath,
                $"Chapter '{request.ExistingPath}' is not on the current board.");
            return;
        }

        var selectionPath = SelectedCard?.RelativePath;
        if (string.Equals(selectionPath, request.ExistingPath, StringComparison.OrdinalIgnoreCase))
            selectionPath = request.ReplacementPath;

        await ExecuteStructuralOperationAsync(
            "Rename card",
            request.ExistingPath,
            () => _structureService.RenameEntryAsync(_workspace.BookTxtPath, request.ExistingPath, request.ReplacementPath),
            selectionPath);
    }

    private async Task CreateChapterAsync(CreateChapterRequest request)
    {
        await ExecuteStructuralOperationAsync(
            "Create chapter",
            request.ChapterPath,
            () => _structureService.CreateNewChapterAsync(_workspace.BookTxtPath, request.ChapterPath, request.Content, request.Index));
    }

    private async Task CreatePartAsync(CreatePartRequest request)
    {
        await ExecuteStructuralOperationAsync(
            "Create part",
            request.PartPath,
            () => _structureService.CreateNewPartAsync(_workspace.BookTxtPath, request.PartPath, request.Title, request.Index));
    }

    private async Task IncludeExistingChapterAsync(IncludeExistingChapterRequest request)
    {
        Func<Task<Result<Unit>>> action;

        if (!string.IsNullOrWhiteSpace(request.PartPath))
        {
            action = () => _structureService.IncludeExistingEntryAfterPartAsync(
                _workspace.BookTxtPath,
                request.ChapterPath,
                request.PartPath!);
        }
        else if (request.Index.HasValue)
        {
            action = () => _structureService.IncludeExistingEntryAsync(
                _workspace.BookTxtPath,
                request.ChapterPath,
                request.Index.Value);
        }
        else
        {
            ReportStructuralFailure(
                "Include chapter",
                request.ChapterPath,
                "Include chapter requires either an index or a part path.");
            return;
        }

        await ExecuteStructuralOperationAsync("Include chapter", request.ChapterPath, action);
    }

    private async Task RemoveFromBookAsync(RemoveChapterRequest request)
    {
        if (!TryFindCurrentChapter(request.ChapterPath, out _))
        {
            ReportStructuralFailure("Remove from book", request.ChapterPath,
                $"Chapter '{request.ChapterPath}' is not on the current board.");
            return;
        }

        await ExecuteStructuralOperationAsync(
            "Remove from book",
            request.ChapterPath,
            () => _structureService.ExcludeEntryAsync(_workspace.BookTxtPath, request.ChapterPath),
            string.Empty);
    }

    private async Task DeleteChapterAsync(DeleteChapterRequest request)
    {
        if (!request.Confirmed)
        {
            ReportStructuralFailure(
                "Delete chapter",
                request.ChapterPath,
                "Delete chapter was rejected because confirmation was not provided.");
            return;
        }

        if (!TryFindCurrentChapter(request.ChapterPath, out _))
        {
            ReportStructuralFailure("Delete chapter", request.ChapterPath,
                $"Chapter '{request.ChapterPath}' is not on the current board.");
            return;
        }

        await ExecuteStructuralOperationAsync(
            "Delete chapter",
            request.ChapterPath,
            () => _structureService.DeleteChapterFileAsync(_workspace.BookTxtPath, request.ChapterPath));
    }

    private async Task ExecuteStructuralOperationAsync(
        string operation,
        string? path,
        Func<Task<Result<Unit>>> action,
        string? selectionPathOverride = null)
    {
        if (!_workspace.HasWorkspace || string.IsNullOrWhiteSpace(_workspace.BookTxtPath))
        {
            ReportStructuralFailure(operation, path, "No workspace is open.");
            return;
        }

        var previousSelectionPath = SelectedCard?.RelativePath;

        try
        {
            using var _ = _manuscriptService.SuppressFileWatcher();

            var result = await action();
            if (!result.IsSuccess)
            {
                ReportStructuralFailure(operation, path, result.Error ?? "Structural action failed.");
                return;
            }

            LastStructuralError = null;
            var reloadResult = await _workspace.ReloadCurrentWorkspaceAsync();
            if (!reloadResult.IsSuccess)
            {
                ReportStructuralFailure(operation, path, reloadResult.Error ?? "Workspace reload failed.");
                return;
            }

            RequestRebuild();
            RestoreSelection(selectionPathOverride ?? previousSelectionPath);
        }
        catch (Exception ex)
        {
            ReportStructuralFailure(operation, path, ex.Message);
        }
    }

    private void RestoreSelection(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            _pendingSelectionPath = null;
            SetSelectedCard(null);
            return;
        }

        var match = _items
            .OfType<ChapterCardItemViewModel>()
            .Select(item => item.Card)
            .FirstOrDefault(card => string.Equals(card.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            _pendingSelectionPath = relativePath;
            SetSelectedCard(null);
            return;
        }

        _pendingSelectionPath = null;
        SetSelectedCard(match);
    }

    private void SetSelectedCard(CardViewModel? card)
    {
        if (ReferenceEquals(_selectedCard, card))
            return;

        if (_selectedCard != null)
            _selectedCard.IsSelected = false;

        _selectedCard = card;

        if (_selectedCard != null)
            _selectedCard.IsSelected = true;

        this.RaisePropertyChanged(nameof(SelectedCard));
    }

    private bool TryFindCurrentChapter(string relativePath, out ChapterViewModel chapter)
    {
        chapter = _workspace.Nodes.FirstOrDefault(node =>
            node.Node.Kind == Hymnal.Core.Models.NodeKind.Chapter &&
            string.Equals(node.Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))!;
        return chapter != null;
    }

    private bool TryResolveReorderIndex(ReorderCardRequest request, out int newIndex, out string? error)
    {
        // Use ALL nodes (parts + chapters) so the computed index matches Book.txt's all-entries list.
        // A chapter-only index would be shifted by the number of part headers before the target.
        var chapters = _workspace.Nodes.ToList();

        var sourceIndex = chapters.FindIndex(node =>
            string.Equals(node.Node.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (sourceIndex < 0)
        {
            newIndex = 0;
            error = $"Chapter '{request.RelativePath}' is not on the current board.";
            return false;
        }

        if (request.NewIndex.HasValue)
        {
            newIndex = request.NewIndex.Value;
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.AfterRelativePath))
        {
            var afterIndex = chapters.FindIndex(node =>
                string.Equals(node.Node.RelativePath, request.AfterRelativePath, StringComparison.OrdinalIgnoreCase));

            if (afterIndex < 0)
            {
                newIndex = 0;
                error = $"Chapter '{request.AfterRelativePath}' was not found on the current board.";
                return false;
            }

            newIndex = sourceIndex > afterIndex ? afterIndex + 1 : afterIndex;
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeRelativePath))
        {
            var beforeIndex = chapters.FindIndex(node =>
                string.Equals(node.Node.RelativePath, request.BeforeRelativePath, StringComparison.OrdinalIgnoreCase));

            if (beforeIndex < 0)
            {
                newIndex = 0;
                error = $"Chapter '{request.BeforeRelativePath}' was not found on the current board.";
                return false;
            }

            newIndex = sourceIndex < beforeIndex ? beforeIndex - 1 : beforeIndex;
            error = null;
            return true;
        }

        newIndex = 0;
        error = "Reorder card requires either a target index or a neighbor path.";
        return false;
    }

    private sealed record ResolvedDropOperation(
        int NewIndex,
        bool IsCrossPart,
        string? ReplacementPath,
        string SelectionPath);

    private bool TryResolveDrop(
        CorkboardDropRequest request,
        out ResolvedDropOperation operation,
        out string? error)
    {
        operation = null!;
        error = null;

        var nodes = _workspace.Nodes.ToList();
        var sourceIndex = FindNodeIndex(nodes, request.RelativePath);
        if (sourceIndex < 0 || nodes[sourceIndex].Node.Kind != NodeKind.Chapter)
        {
            error = $"Chapter '{request.RelativePath}' is not on the current board.";
            return false;
        }

        if (IsSelfTarget(request))
        {
            error = "A chapter cannot be dropped onto itself.";
            return false;
        }

        if (!TryResolveTargetIndex(request, nodes, sourceIndex, out var newIndex, out error))
            return false;

        var sourcePartPath = FindOwningPartPath(nodes, sourceIndex);
        var targetPartPath = ResolveTargetPartPath(request, nodes, newIndex);
        var isCrossPart = !string.Equals(sourcePartPath ?? string.Empty, targetPartPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var replacementPath = isCrossPart
            ? BuildReplacementPath(request.RelativePath, targetPartPath)
            : null;

        operation = new ResolvedDropOperation(
            newIndex,
            isCrossPart,
            replacementPath,
            replacementPath ?? request.RelativePath);
        return true;
    }

    private static bool IsSelfTarget(CorkboardDropRequest request)
        => string.Equals(request.RelativePath, request.AfterRelativePath, StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.RelativePath, request.BeforeRelativePath, StringComparison.OrdinalIgnoreCase);

    private static int FindNodeIndex(IReadOnlyList<ChapterViewModel> nodes, string relativePath)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private bool TryResolveTargetIndex(
        CorkboardDropRequest request,
        IReadOnlyList<ChapterViewModel> nodes,
        int sourceIndex,
        out int newIndex,
        out string? error)
    {
        if (!string.IsNullOrWhiteSpace(request.AfterRelativePath))
        {
            var afterIndex = FindNodeIndex(nodes, request.AfterRelativePath);
            if (afterIndex < 0)
            {
                newIndex = 0;
                error = $"Drop target '{request.AfterRelativePath}' was not found in Book.txt.";
                return false;
            }

            newIndex = sourceIndex > afterIndex ? afterIndex + 1 : afterIndex;
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeRelativePath))
        {
            var beforeIndex = FindNodeIndex(nodes, request.BeforeRelativePath);
            if (beforeIndex < 0)
            {
                newIndex = 0;
                error = $"Drop target '{request.BeforeRelativePath}' was not found in Book.txt.";
                return false;
            }

            newIndex = sourceIndex < beforeIndex ? beforeIndex - 1 : beforeIndex;
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.TargetPartPath))
        {
            var partIndex = FindNodeIndex(nodes, request.TargetPartPath);
            if (partIndex < 0 || nodes[partIndex].Node.Kind != NodeKind.Part)
            {
                newIndex = 0;
                error = $"Target Part '{request.TargetPartPath}' was not found in Book.txt.";
                return false;
            }

            newIndex = partIndex + 1;
            error = null;
            return true;
        }

        newIndex = sourceIndex;
        error = "Drop card requires a target Part or neighbor path.";
        return false;
    }

    private static string? FindOwningPartPath(IReadOnlyList<ChapterViewModel> nodes, int chapterIndex)
    {
        for (var i = chapterIndex - 1; i >= 0; i--)
        {
            if (nodes[i].Node.Kind == NodeKind.Part)
                return nodes[i].Node.RelativePath;
        }

        return null;
    }

    private static string? ResolveTargetPartPath(
        CorkboardDropRequest request,
        IReadOnlyList<ChapterViewModel> nodes,
        int targetIndex)
    {
        if (!string.IsNullOrWhiteSpace(request.TargetPartPath))
            return request.TargetPartPath;

        if (!string.IsNullOrWhiteSpace(request.AfterRelativePath))
        {
            var afterIndex = FindNodeIndex(nodes, request.AfterRelativePath);
            if (afterIndex >= 0)
                return nodes[afterIndex].Node.Kind == NodeKind.Part
                    ? nodes[afterIndex].Node.RelativePath
                    : FindOwningPartPath(nodes, afterIndex);
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeRelativePath))
        {
            var beforeIndex = FindNodeIndex(nodes, request.BeforeRelativePath);
            if (beforeIndex >= 0)
                return FindOwningPartPath(nodes, beforeIndex);
        }

        var priorIndex = Math.Min(targetIndex - 1, nodes.Count - 1);
        for (var i = priorIndex; i >= 0; i--)
        {
            if (nodes[i].Node.Kind == NodeKind.Part)
                return nodes[i].Node.RelativePath;
        }

        return null;
    }

    private static string BuildReplacementPath(string sourcePath, string? targetPartPath)
    {
        var fileName = Path.GetFileName(sourcePath.Replace('\\', '/'));
        var targetFolder = GetPartFolderPrefix(targetPartPath);
        return string.IsNullOrWhiteSpace(targetFolder)
            ? fileName
            : $"{targetFolder}/{fileName}";
    }

    private static bool IsCommittedMoveManifestFailure(string message)
        => message.Contains("manifest save after file move, Book.txt write, and registry update", StringComparison.OrdinalIgnoreCase);

    private void ReportStructuralFailure(string operation, string? path, string message)
    {
        var bookTxtPath = _workspace.BookTxtPath;
        LastStructuralError = new CorkboardStructuralError(operation, path, message, bookTxtPath);

        var target = string.IsNullOrWhiteSpace(path)
            ? $"{operation} in '{bookTxtPath}'"
            : $"{operation} for '{path}' in '{bookTxtPath}'";
        _notificationService.ShowError($"{target}: {message}");
    }

    private void ReportUnexpectedError(Exception ex) => ReportStructuralFailure("Corkboard operation", null, ex.Message);

    public void Dispose()
    {
        _activeInlineCreate?.Dispose();
        _activeInlineCreate = null;
        CorkboardItemViewModel.DisposeItems(_items.Where(i => i is not InlineCreateItemViewModel).ToList());
        Disposables.Dispose();
    }
}
