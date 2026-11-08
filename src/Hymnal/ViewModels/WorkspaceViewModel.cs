using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Reactive.Disposables;
using DynamicData;
using DynamicData.Binding;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Unit = Hymnal.Core.Common.Unit;
using PathHelper = Hymnal.Core.Common.PathHelper;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

public class WorkspaceViewModel : ViewModelBase
{
    private readonly ManuscriptService _manuscriptService;
    private readonly IBookTxtStructureService _structureService;
    private readonly IFilePickerService _filePicker;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IFolderPickerService _folderPicker;
    private readonly INotificationService _notificationService;
    private readonly EditorViewModel _editor;
    private readonly ChapterRegistryService _registryService;
    private readonly PhaseDataService _phaseDataService;
    private readonly TargetsService _targetsService;
    private readonly WordCountService _wordCountService;
    private readonly WordCountHistoryService _historyService;
    private readonly IExclusionManifestService _exclusionManifestService;
    private readonly IOrphanFileDiscoveryService _orphanFileDiscoveryService;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly ProjectInitializer? _projectInitializer;
    private readonly IProjectInitPrompt? _initPrompt;
    private ManuscriptModel? _model;
    private int _workspaceGeneration;
    private Task? _hydrationTask;
    private bool _isSwitching;
    private readonly Subject<Unit> _workspaceChanged = new();
    private CompositeDisposable _partSubscriptions = new();

    public IObservable<Unit> WorkspaceChanged => _workspaceChanged.AsObservable();
    private readonly ObservableCollectionExtended<ChapterViewModel> _nodes = new();
    private readonly ObservableCollectionExtended<ChapterViewModel> _visibleNodes = new();
    private readonly Dictionary<string, ChapterViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    public ReadOnlyObservableCollection<ChapterViewModel> Nodes { get; }
    public ReadOnlyObservableCollection<ChapterViewModel> VisibleNodes { get; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private ProjectProfile _profile = ProjectProfile.Novel;
    /// <summary>Profile of the open workspace's project kind (Novel when none is open).</summary>
    public ProjectProfile Profile
    {
        get => _profile;
        private set => this.RaiseAndSetIfChanged(ref _profile, value);
    }

    private bool _hasWorkspace;
    public bool HasWorkspace
    {
        get => _hasWorkspace;
        private set => this.RaiseAndSetIfChanged(ref _hasWorkspace, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    private ChapterViewModel? _selectedNode;
    public ChapterViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    public string WorkspaceRoot => _model?.WorkspaceRoot ?? string.Empty;

    public string BookTxtPath => _model?.BookTxtPath ?? string.Empty;

    public string ManuscriptRoot => _model?.ManuscriptRoot ?? string.Empty;

    private string? _workspaceName;
    public string? WorkspaceName
    {
        get => _workspaceName;
        private set => this.RaiseAndSetIfChanged(ref _workspaceName, value);
    }

    private int _totalWordCount;
    public int TotalWordCount
    {
        get => _totalWordCount;
        private set => this.RaiseAndSetIfChanged(ref _totalWordCount, value);
    }

    private readonly ObservableAsPropertyHelper<string> _totalWordCountDisplay;
    /// <summary>Formatted book total for the CHAPTERS header, e.g. "12,345 w".</summary>
    public string TotalWordCountDisplay => _totalWordCountDisplay.Value;

    private readonly ObservableAsPropertyHelper<string> _totalWordCountTooltip;
    /// <summary>Full tooltip for the book total word count label.</summary>
    public string TotalWordCountTooltip => _totalWordCountTooltip.Value;

    private IReadOnlyList<StatusCount> _bookStatusSummary = Array.Empty<StatusCount>();
    /// <summary>Status breakdown for all chapters in the book, used by the Book header pie chart.</summary>
    public IReadOnlyList<StatusCount> BookStatusSummary
    {
        get => _bookStatusSummary;
        private set => this.RaiseAndSetIfChanged(ref _bookStatusSummary, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenWorkspaceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CloseWorkspaceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectBookCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleChaptersPaneCommand { get; }
    public ReactiveCommand<CreateChapterRequest, System.Reactive.Unit> CreateChapterCommand { get; }
    public ReactiveCommand<CreatePartRequest, System.Reactive.Unit> CreatePartCommand { get; }
    public ReactiveCommand<IncludeExistingChapterRequest, System.Reactive.Unit> IncludeExistingFileCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> IncludeExcludedChapterCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> RemoveFromBookCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> RemoveMissingEntryCommand { get; }
    public ReactiveCommand<ReorderCardRequest, System.Reactive.Unit> ReorderChapterCommand { get; }
    public ReactiveCommand<RenameChapterRequest, System.Reactive.Unit> RenameChapterCommand { get; }
    public ReactiveCommand<RenamePartRequest, System.Reactive.Unit> RenamePartCommand { get; }

    private ChapterViewModel? _targetFlyoutChapter;
    private IDisposable? _targetFlyoutSubscription;
    /// <summary>Chapter whose shared target flyout is open in the sidebar.</summary>
    public ChapterViewModel? TargetFlyoutChapter
    {
        get => _targetFlyoutChapter;
        private set => SetTargetFlyoutChapter(value);
    }

    /// <summary>Null-safe open state for the shared target flyout Popup.</summary>
    public bool IsTargetFlyoutOpen
    {
        get => TargetFlyoutChapter?.IsTargetFlyoutOpen ?? false;
        set
        {
            if (value)
                return;

            CloseTargetFlyout();
        }
    }

    private bool _isChaptersPaneVisible = true;
    /// <summary>True when the CHAPTERS list content area is expanded in the left sidebar.</summary>
    public bool IsChaptersPaneVisible
    {
        get => _isChaptersPaneVisible;
        set
        {
            if (_isChaptersPaneVisible == value)
                return;

            this.RaiseAndSetIfChanged(ref _isChaptersPaneVisible, value);
            _ = PersistChaptersPaneVisibleAsync(value);
        }
    }

    public WorkspaceViewModel(
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
        IExclusionManifestService? exclusionManifestService = null,
        IOrphanFileDiscoveryService? orphanFileDiscoveryService = null,
        ProjectSettingsService? projectSettings = null,
        ProjectInitializer? projectInitializer = null,
        IProjectInitPrompt? initPrompt = null)
    {
        _manuscriptService = manuscriptService;
        _structureService = structureService;
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _folderPicker = folderPicker;
        _notificationService = notificationService;
        _editor = editor;
        _registryService = registryService;
        _phaseDataService = phaseDataService;
        _targetsService = targetsService;
        _wordCountService = wordCountService;
        _historyService = historyService;
        _exclusionManifestService = exclusionManifestService ?? new ExclusionManifestService(new MetadataStore());
        _orphanFileDiscoveryService = orphanFileDiscoveryService ?? new OrphanFileDiscoveryService();
        _projectSettings = projectSettings;
        _projectInitializer = projectInitializer;
        _initPrompt = initPrompt;
        _editor.HasWorkspace = false;

        Nodes = new ReadOnlyObservableCollection<ChapterViewModel>(_nodes);
        VisibleNodes = new ReadOnlyObservableCollection<ChapterViewModel>(_visibleNodes);

        ToggleChaptersPaneCommand = ReactiveCommand.Create(
            () => { IsChaptersPaneVisible = !IsChaptersPaneVisible; },
            this.WhenAnyValue(x => x.HasWorkspace));

        OpenWorkspaceCommand = ReactiveCommand.CreateFromTask(OpenWorkspaceAsync);
        CloseWorkspaceCommand = ReactiveCommand.CreateFromTask(CloseWorkspaceAsync, this.WhenAnyValue(x => x.HasWorkspace));
        SelectBookCommand = ReactiveCommand.CreateFromTask(SelectBookAsync);

        CreateChapterCommand = ReactiveCommand.CreateFromTask<CreateChapterRequest>(CreateChapterAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        CreatePartCommand = ReactiveCommand.CreateFromTask<CreatePartRequest>(CreatePartAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        IncludeExistingFileCommand = ReactiveCommand.CreateFromTask<IncludeExistingChapterRequest>(IncludeExistingFileAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        IncludeExcludedChapterCommand = ReactiveCommand.CreateFromTask<string>(IncludeExcludedChapterAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        RemoveFromBookCommand = ReactiveCommand.CreateFromTask<string>(RemoveFromBookAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        RemoveMissingEntryCommand = ReactiveCommand.CreateFromTask<string>(RemoveMissingEntryAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        ReorderChapterCommand = ReactiveCommand.CreateFromTask<ReorderCardRequest>(ReorderChapterAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        RenameChapterCommand = ReactiveCommand.CreateFromTask<RenameChapterRequest>(RenameChapterAsync,
            this.WhenAnyValue(x => x.HasWorkspace));
        RenamePartCommand = ReactiveCommand.CreateFromTask<RenamePartRequest>(RenamePartAsync,
            this.WhenAnyValue(x => x.HasWorkspace));

        Disposables.Add(
            CreateChapterCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            CreatePartCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            IncludeExistingFileCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            IncludeExcludedChapterCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            RemoveFromBookCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            RemoveMissingEntryCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            ReorderChapterCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            RenameChapterCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            RenamePartCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));

        Disposables.Add(
            OpenWorkspaceCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            CloseWorkspaceCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(
            SelectBookCommand.ThrownExceptions
                .Subscribe(Observer.Create<Exception>(ex => _notificationService.ShowError(ex.Message))));
        Disposables.Add(Disposable.Create(() => _workspaceChanged.Dispose()));
        Disposables.Add(Disposable.Create(() => _targetFlyoutSubscription?.Dispose()));
        Disposables.Add(Disposable.Create(() => _partSubscriptions.Dispose()));

        Disposables.Add(
            this.WhenAnyValue(x => x.SelectedNode)
                .Skip(1)
                .Where(_ => !_isSwitching)
                .Subscribe(node =>
                {
                    if (node != null)
                        _ = TrySwitchChapterAsync(node);
                }));

        // TotalWordCountDisplay OAPH — updates whenever TotalWordCount changes.
        _totalWordCountDisplay = this.WhenAnyValue(x => x.TotalWordCount)
            .Select(c => $"{c:N0} w")
            .ToProperty(this, x => x.TotalWordCountDisplay);
        Disposables.Add(_totalWordCountDisplay);

        _totalWordCountTooltip = this.WhenAnyValue(x => x.TotalWordCount)
            .Select(c => $"{c:N0} words total")
            .ToProperty(this, x => x.TotalWordCountTooltip);
        Disposables.Add(_totalWordCountTooltip);

        // Subscribe to EditorViewModel.Saved: recount, update totals, record history,
        // and refresh sidebar title if the heading line changed.
        // Fires on the UI thread (Subject.OnNext is called from SaveAsync).
        Disposables.Add(
            _editor.Saved.Subscribe(_ =>
            {
                // Book.txt save: reload workspace first, before the activeNode guard.
                if (_editor.IsBookSelected && _model != null)
                {
                    var root = _model.WorkspaceRoot;
                    var bookTxtPath = _model.BookTxtPath;
                    var task = ReloadWorkspaceAsync(root, bookTxtPath);
                    task.ContinueWith(t =>
                    {
                        if (!t.IsCompletedSuccessfully && t.Exception != null)
                            _notificationService.ShowError(
                                $"Failed to reload workspace after Book.txt edit: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                    }, TaskScheduler.Default);
                    return;
                }

                var activeNode = _editor.ActiveNode;
                if (activeNode == null || _model == null) return;

                if (!_nodesByPath.TryGetValue(activeNode.RelativePath, out var vm))
                    return;

                var count = _wordCountService.CountWords(_editor.Text);
                vm.UpdateWordCount(count);

                // Refresh sidebar title if the # heading changed in the saved content.
                var newTitle = BookTxtParser.ExtractTitleFromText(_editor.Text);
                if (newTitle != null && !string.Equals(newTitle, vm.Node.Title, StringComparison.Ordinal))
                {
                    var updatedNode = vm.Node with { Title = newTitle };
                    vm.UpdateNode(updatedNode);
                    _editor.UpdateActiveNode(updatedNode);
                }

                UpdateTotals();

                if (!string.IsNullOrEmpty(vm.Uuid))
                {
                    var workspaceRoot = _model.WorkspaceRoot;
                    var uuid = vm.Uuid;
                    var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
                    var histTask = _historyService.AppendAsync(workspaceRoot, uuid, date, count);
                    histTask.ContinueWith(t =>
                        {
                            if (!t.IsCompletedSuccessfully && t.Exception != null)
                            {
                                _notificationService.ShowError(
                                    $"Failed to save word count history: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                            }
                        }, TaskScheduler.Default);
                }

            }));
    }

    // ── Chapter switch ────────────────────────────────────────────────────────

    private async Task TrySwitchChapterAsync(ChapterViewModel viewModel)
    {
        var node = viewModel.Node;
        var previousNode = _editor.ActiveNode != null
            && _nodesByPath.TryGetValue(_editor.ActiveNode.RelativePath, out var prev)
            ? prev
            : null;

        if (node.IsExcluded)
        {
            _isSwitching = true;
            SelectedNode = previousNode;
            _isSwitching = false;
            return;
        }

        // Missing chapter: show warning overlay instead of deselecting.
        if (node.IsMissing)
        {
            if (_editor.IsDirty)
            {
                try { await _editor.SaveAsync(); }
                catch { /* Don't abort — still show the missing warning */ }
            }
            _editor.OpenMissingChapter(node);
            return;
        }

        if (_editor.IsDirty)
        {
            try
            {
                await _editor.SaveAsync();
            }
            catch
            {
                _isSwitching = true;
                SelectedNode = previousNode;
                _isSwitching = false;
                return;
            }
        }

        var absolutePath = ResolveAbsolutePath(node);
        try
        {
            await _editor.OpenChapterAsync(node, absolutePath);
        }
        catch (Exception ex)
        {
            // The editor keeps the previous chapter (and its watcher); put the sidebar back on it.
            _notificationService.ShowError($"Could not open '{node.Title}': {ex.Message}");
            _isSwitching = true;
            SelectedNode = previousNode;
            _isSwitching = false;
            return;
        }

        // Only persist as "last chapter" for regular chapters, not parts.
        if (node.Kind == NodeKind.Chapter)
            await _settingsStore.SetAsync("lastChapterPath", Path.GetFullPath(absolutePath));
    }

    private async Task SelectBookAsync()
    {
        if (_editor.IsDirty)
        {
            try { await _editor.SaveAsync(); }
            catch { return; }
        }

        _isSwitching = true;
        SelectedNode = null;
        _isSwitching = false;
        await _editor.SelectBookAsync(_model?.BookTxtPath ?? string.Empty);
    }

    private string ResolveAbsolutePath(ChapterNode node) =>
        Path.Combine(_model!.ManuscriptRoot, node.RelativePath);

    public void ClearChapterSelectionForExternalDocument()
    {
        _isSwitching = true;
        SelectedNode = null;
        _isSwitching = false;
    }

    // ── Workspace open ────────────────────────────────────────────────────────

    /// <summary>
    /// Reload the current workspace from disk and rebuild the chapter list without
    /// changing the editor's active file.
    /// </summary>
    public virtual async Task<Result<Unit>> ReloadCurrentWorkspaceAsync()
    {
        if (_model == null)
            return Result<Unit>.Ok(Unit.Default);

        return await ReloadWorkspaceAsync(_model.WorkspaceRoot, _model.BookTxtPath, reselectBook: false);
    }

    /// <summary>
    /// Lightweight reorder that re-reads Book.txt and rearranges the existing
    /// <see cref="Nodes"/> to match, without re-hydrating the registry, phases,
    /// targets, or word counts.
    /// </summary>
    public virtual async Task<Result<Unit>> ReorderNodesAsync()
    {
        if (_model == null)
            return Result<Unit>.Ok(Unit.Default);

        try
        {
            var bookTxtPath = _model.BookTxtPath;
            if (string.IsNullOrWhiteSpace(bookTxtPath) || !File.Exists(bookTxtPath))
                return Result<Unit>.Fail("Book.txt not found.");

            var lines = await File.ReadAllLinesAsync(bookTxtPath).ConfigureAwait(false);
            var newOrder = lines
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => l.Replace('\\', '/'))
                .ToList();

            var reorderedActive = new List<ChapterNode>(newOrder.Count);
            foreach (var path in newOrder)
            {
                if (_nodesByPath.TryGetValue(path, out var vm))
                    reorderedActive.Add(vm.Node with { IsExcluded = false });
            }

            var projectedExcluded = _nodes
                .Where(vm => vm.Node.IsExcluded)
                .Select(vm => vm.Node)
                .ToList();

            var reorderedNodes = MergeProjectedSidebarNodes(reorderedActive, projectedExcluded);
            var reordered = new List<ChapterViewModel>(reorderedNodes.Count);
            foreach (var node in reorderedNodes)
            {
                if (_nodesByPath.TryGetValue(node.RelativePath, out var vm))
                {
                    vm.UpdateNode(node);
                    reordered.Add(vm);
                }
            }

            using (_nodes.SuspendNotifications())
            {
                _nodes.Clear();
                foreach (var vm in reordered)
                    _nodes.Add(vm);
            }

            RebuildNodeLookup();
            UpdateTotals();
            UpdateVisibility();
            _workspaceChanged.OnNext(Unit.Default);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reload the workspace from disk (called after Book.txt is saved) without disturbing
    /// the editor. Keeps Book.txt open so the user can continue editing.
    /// </summary>
    private async Task<Result<Unit>> ReloadWorkspaceAsync(string workspaceRoot, string bookTxtPath, bool reselectBook = true)
    {
        var result = await _manuscriptService.LoadWorkspaceAsync(workspaceRoot);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError($"Failed to reload workspace: {result.Error}");
            return Result<Unit>.Fail(result.Error!);
        }

        await LoadProfileAsync(workspaceRoot);

        // Rebuild node list without closing the editor.
        BindModel(result.Value!);

        if (reselectBook)
        {
            // Re-open Book.txt in the editor so the title bar and watcher stay current.
            await _editor.SelectBookAsync(bookTxtPath);
        }

        return Result<Unit>.Ok(Unit.Default);
    }

    private async Task OpenWorkspaceAsync()
    {
        var path = await _folderPicker.PickFolderAsync();
        if (path == null) return;

        if (!await InitializeProjectIfNeededAsync(path)) return;

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _manuscriptService.LoadWorkspaceAsync(path);
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error;
                _notificationService.ShowError(result.Error!);
                return;
            }

            ErrorMessage = null;
            await _settingsStore.SetAsync("lastWorkspacePath", path);
            await LoadProfileAsync(result.Value!.WorkspaceRoot);
            _editor.HasWorkspace = true;
            BindModel(result.Value!);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-reads the workspace's project settings, updates <see cref="Profile"/>, and rebuilds
    /// the chapter list so every chapter picks up the new profile. The editor's active file is kept,
    /// and the selection moves to the rebuilt node for the same path so it never points at a
    /// disposed view-model carrying the old profile.
    /// </summary>
    public async Task<Result<Unit>> ReloadProjectAsync()
    {
        if (_model == null)
            return Result<Unit>.Ok(Unit.Default);

        var selectedPath = SelectedNode?.Node.RelativePath ?? _editor.ActiveNode?.RelativePath;

        // ReloadWorkspaceAsync re-reads project.json before rebinding.
        var result = await ReloadWorkspaceAsync(WorkspaceRoot, BookTxtPath, reselectBook: false);
        if (!result.IsSuccess)
            return result;

        var hydrationTask = _hydrationTask;
        if (hydrationTask != null)
            await hydrationTask;

        if (selectedPath != null)
            SelectReplacementNodeIfPresent(selectedPath);

        return result;
    }

    /// <summary>
    /// Loads project.json for <paramref name="workspaceRoot"/> into <see cref="Profile"/>.
    /// Missing settings service or file → Novel; unreadable file → Novel plus an info notice.
    /// </summary>
    private async Task LoadProfileAsync(string workspaceRoot)
    {
        if (_projectSettings == null)
        {
            Profile = ProjectProfile.Novel;
            return;
        }

        var result = await _projectSettings.LoadAsync(workspaceRoot);
        if (result.IsSuccess)
        {
            Profile = ProjectProfile.For(result.Value!);
            return;
        }

        _notificationService.ShowInfo($"Couldn't read project settings ({result.Error}). Opening as a novel.");
        Profile = ProjectProfile.Novel;
    }

    /// <summary>
    /// Offers to initialize a folder that has no Book.txt. Returns false when the user
    /// declines or cancels, or initialization fails; true when loading should continue.
    /// </summary>
    private async Task<bool> InitializeProjectIfNeededAsync(string path)
    {
        if (_initPrompt == null || _projectInitializer == null || !ProjectInitializer.NeedsInitialization(path))
            return true;

        if (!await _initPrompt.ConfirmInitializeAsync(path))
            return false;

        var kind = await _initPrompt.ChooseProjectKindAsync();
        if (kind == null)
            return false;

        var init = await _projectInitializer.InitializeAsync(path, kind.Value);
        if (!init.IsSuccess)
        {
            _notificationService.ShowError(init.Error!);
            return false;
        }

        return true;
    }

    private async Task CloseWorkspaceAsync()
    {
        if (!HasWorkspace) return;

        if (_editor.IsDirty)
        {
            try { await _editor.SaveAsync(); }
            catch { return; }
        }

        _manuscriptService.UnloadWorkspace();
        _editor.CloseChapter();
        _editor.HasWorkspace = false;

        foreach (var vm in _nodes) vm.Dispose();
        _model = null;
        _nodes.Clear();
        _nodesByPath.Clear();
        TargetFlyoutChapter = null;
        _isSwitching = false;
        SelectedNode = null;
        HasWorkspace = false;
        WorkspaceName = null;
        Profile = ProjectProfile.Novel;
        this.RaisePropertyChanged(nameof(WorkspaceRoot));
        this.RaisePropertyChanged(nameof(BookTxtPath));
        _workspaceChanged.OnNext(Unit.Default);
        ErrorMessage = null;
        IsLoading = false;

        await _settingsStore.SetAsync<string?>("lastWorkspacePath", null);
        await _settingsStore.SetAsync<string?>("lastChapterPath", null);
        TotalWordCount = 0;
    }

    // ── Init / restore ────────────────────────────────────────────────────────

    public async Task InitAsync()
    {
        await RestorePaneSettingsAsync().ConfigureAwait(false);

        var lastPath = await _settingsStore.GetAsync<string>("lastWorkspacePath");
        if (lastPath == null) return;

        IsLoading = true;
        try
        {
            var result = await _manuscriptService.LoadWorkspaceAsync(lastPath);
            if (!result.IsSuccess) return;

            await LoadProfileAsync(result.Value!.WorkspaceRoot);
            BindModel(result.Value!);
            _editor.HasWorkspace = true;

            var hydrationTask = _hydrationTask;
            if (hydrationTask != null)
                await hydrationTask;

            var lastChapterPath = await _settingsStore.GetAsync<string>("lastChapterPath");
            if (lastChapterPath != null)
            {
                ChapterViewModel? match = null;
                foreach (var vm in _nodes)
                {
                    if (vm.Node.Kind == NodeKind.Chapter && !vm.Node.IsMissing &&
                        PathHelper.IsSamePath(ResolveAbsolutePath(vm.Node), lastChapterPath))
                    {
                        match = vm;
                        break;
                    }
                }

                if (match != null)
                {
                    try
                    {
                        await _editor.OpenChapterAsync(match.Node, ResolveAbsolutePath(match.Node));

                        _isSwitching = true;
                        SelectedNode = match;
                        _isSwitching = false;
                    }
                    catch
                    {
                        // Restore failure is silent on startup.
                    }
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Model binding ─────────────────────────────────────────────────────────

    private void BindModel(ManuscriptModel model)
    {
        _model = model;
        HasWorkspace = true;
        WorkspaceName = Path.GetFileName(model.WorkspaceRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        this.RaisePropertyChanged(nameof(WorkspaceRoot));
        this.RaisePropertyChanged(nameof(BookTxtPath));
        _workspaceChanged.OnNext(Unit.Default);

        // Track registry/phase hydration so restore waits for the active workspace.
        var generation = ++_workspaceGeneration;
        _hydrationTask = LoadRegistryAndPhaseDataAsync(model, generation, Profile);
    }

    private async Task LoadRegistryAndPhaseDataAsync(ManuscriptModel model, int generation, ProjectProfile profile)
    {
        try
        {
            var activeNodes = model.Nodes.Items.OrderBy(n => n.Index).ToList();
            var activePaths = activeNodes.Select(n => n.RelativePath).ToList();

            var registry = await _registryService.LoadAsync(model.WorkspaceRoot).ConfigureAwait(false);
            var originalRegistry = new Dictionary<string, ChapterRegistryEntry>(registry);

            // Registry continuity is defined only by Book.txt membership. Excluded sidebar
            // projections remain visible in the UI but do not get fresh UUID assignments.
            registry = _registryService.ReconcileOrphans(registry, activePaths);

            var orphanedEntries = registry.Values.Where(entry => entry.Orphaned).ToList();
            var matchedOrphans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in activeNodes)
            {
                var hasCurrentPath = registry.Values.Any(entry =>
                    string.Equals(entry.CurrentPath, node.RelativePath, StringComparison.OrdinalIgnoreCase));

                if (!hasCurrentPath)
                {
                    var titleMatches = orphanedEntries
                        .Where(entry => !matchedOrphans.Contains(entry.Uuid)
                            && !string.IsNullOrWhiteSpace(entry.Title)
                            && string.Equals(entry.Title, node.Title, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (titleMatches.Count == 1)
                    {
                        registry = _registryService.ReconcileRename(
                            registry,
                            titleMatches[0].CurrentPath,
                            node.RelativePath);
                        matchedOrphans.Add(titleMatches[0].Uuid);
                    }
                }

                _registryService.AssignUuid(registry, node.RelativePath, node.Title);
            }

            if (RegistryChanged(originalRegistry, registry))
                await _registryService.SaveAsync(model.WorkspaceRoot, registry).ConfigureAwait(false);

            var phases = await _phaseDataService.LoadAsync(model.WorkspaceRoot).ConfigureAwait(false);
            var targets = await _targetsService.LoadAsync(model.WorkspaceRoot).ConfigureAwait(false);
            var projectedNodes = await ProjectSidebarNodesAsync(model, activeNodes, activePaths).ConfigureAwait(false);

            if (generation != _workspaceGeneration)
                return;

            Dictionary<string, List<string>>? collapsedMap = null;
            try { collapsedMap = await _settingsStore.GetAsync<Dictionary<string, List<string>>>("partCollapsedStates").ConfigureAwait(false); } catch { }
            var collapsedPartPaths = collapsedMap != null && collapsedMap.TryGetValue(model.WorkspaceRoot, out var savedPaths)
                ? new HashSet<string>(savedPaths, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var vms = projectedNodes
                .Select(node =>
                {
                    var uuid = TryGetRegistryUuid(registry, node.RelativePath);
                    phases.TryGetValue(uuid, out var phaseData);
                    var target = string.IsNullOrWhiteSpace(uuid)
                        ? null
                        : _targetsService.GetTarget(targets, uuid);
                    return new ChapterViewModel(
                        node,
                        uuid,
                        phaseData,
                        _phaseDataService,
                        _targetsService,
                        _settingsStore,
                        _notificationService,
                        model.WorkspaceRoot,
                        target,
                        profile);
                })
                .ToList();

            foreach (var vm in vms)
            {
                vm.TargetFlyoutOpenRequested += OpenTargetFlyout;
                vm.TargetFlyoutCloseRequested += CloseTargetFlyout;
            }

            var countTargets = vms
                .Where(vm => vm.Node.Kind == NodeKind.Chapter && !vm.Node.IsMissing && !vm.Node.IsExcluded)
                .Select(vm => (Vm: vm, AbsPath: Path.Combine(model.ManuscriptRoot, vm.Node.RelativePath)))
                .ToList();

            if (countTargets.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var counts = await Task.WhenAll(countTargets.Select(async target =>
                        {
                            try
                            {
                                var content = await File.ReadAllTextAsync(target.AbsPath).ConfigureAwait(false);
                                // Prose only: a story file's trailing metadata block is not counted.
                                return (target.Vm, Count: _wordCountService.CountWords(StoryFileFormat.Split(content).Prose));
                            }
                            catch
                            {
                                return (target.Vm, Count: -1);
                            }
                        })).ConfigureAwait(false);

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_workspaceGeneration != generation)
                                return;

                            foreach (var (vm, count) in counts)
                            {
                                if (count >= 0)
                                    vm.UpdateWordCount(count);
                            }

                            UpdateTotals();
                        });
                    }
                    catch
                    {
                        // Intentionally silent — chapter stays at '—' in sidebar.
                    }
                });
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _workspaceGeneration)
                    return;

                foreach (var old in _nodes) old.Dispose();
                _nodes.Clear();
                foreach (var vm in vms) _nodes.Add(vm);
                RebuildNodeLookup();
                UpdateTotals();

                foreach (var vm in _nodes.Where(v => v.Node.Kind == NodeKind.Part))
                    vm.IsExpanded = !collapsedPartPaths.Contains(vm.Node.RelativePath);

                _partSubscriptions.Dispose();
                _partSubscriptions = new CompositeDisposable();
                foreach (var vm in _nodes)
                {
                    if (vm.Node.Kind == NodeKind.Part)
                    {
                        _partSubscriptions.Add(
                            vm.WhenAnyValue(x => x.IsExpanded)
                                .Skip(1)
                                .Subscribe(__ =>
                                {
                                    UpdateVisibility();
                                    var root = _model?.WorkspaceRoot;
                                    if (root == null) return;
                                    var collapsed = _nodes
                                        .Where(v => v.Node.Kind == NodeKind.Part && !v.IsExpanded)
                                        .Select(v => v.Node.RelativePath)
                                        .ToList();
                                    _ = PersistPartExpandedStatesAsync(root, collapsed);
                                }));
                    }
                    else
                    {
                        _partSubscriptions.Add(
                            vm.WhenAnyValue(x => x.Status)
                                .Skip(1)
                                .Subscribe(_ => UpdateTotals()));
                    }
                }
                UpdateVisibility();
            });
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Failed to load chapter registry: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<ChapterNode>> ProjectSidebarNodesAsync(
        ManuscriptModel model,
        IReadOnlyList<ChapterNode> activeNodes,
        IReadOnlyList<string> activePaths)
    {
        var manifest = await _exclusionManifestService.LoadAsync(model.ManuscriptRoot).ConfigureAwait(false);
        if (!manifest.IsSuccess && !string.Equals(model.WorkspaceRoot, model.ManuscriptRoot, StringComparison.OrdinalIgnoreCase))
            manifest = await _exclusionManifestService.LoadAsync(model.WorkspaceRoot).ConfigureAwait(false);

        if (!manifest.IsSuccess)
            throw new InvalidOperationException(manifest.Error);

        if (manifest.Value!.ExcludedPaths.Length == 0)
            return activeNodes;

        var discovered = await _orphanFileDiscoveryService
            .DiscoverAsync(model.ManuscriptRoot, activePaths)
            .ConfigureAwait(false);

        var discoveredByPath = discovered.ToDictionary(info => info.RelativePath, StringComparer.OrdinalIgnoreCase);
        var projectedExcluded = manifest.Value.ExcludedPaths
            .Where(path => discoveredByPath.ContainsKey(path))
            .Select(path => BuildExcludedProjectionNode(path, discoveredByPath[path]))
            .ToList();

        if (projectedExcluded.Count == 0)
            return activeNodes;

        return MergeProjectedSidebarNodes(activeNodes, projectedExcluded);
    }

    private static ChapterNode BuildExcludedProjectionNode(string relativePath, OrphanFileInfo orphan)
    {
        return new ChapterNode(
            Key: relativePath,
            RelativePath: relativePath,
            Title: orphan.Title,
            Kind: NodeKind.Chapter,
            IsMissing: false,
            Index: 0)
        {
            IsExcluded = true
        };
    }

    private static IReadOnlyList<ChapterNode> MergeProjectedSidebarNodes(
        IReadOnlyList<ChapterNode> activeNodes,
        IReadOnlyList<ChapterNode> projectedExcluded)
    {
        var activeInOrder = activeNodes.OrderBy(node => node.Index).ToList();
        var merged = new List<ChapterNode>(activeInOrder.Count + projectedExcluded.Count);

        var partFolders = activeInOrder
            .Where(node => node.Kind == NodeKind.Part)
            .Select(node => Path.GetDirectoryName(node.RelativePath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groupedExcluded = projectedExcluded
            .GroupBy(node => DetectPartFolder(node.RelativePath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var lastIndexByPartFolder = activeInOrder
            .Select((node, index) => new { node, index })
            .Where(x => !string.IsNullOrWhiteSpace(DetectPartFolder(x.node.RelativePath)))
            .GroupBy(x => DetectPartFolder(x.node.RelativePath)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(x => x.index), StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < activeInOrder.Count; index++)
        {
            var node = activeInOrder[index];
            merged.Add(node);

            var folder = DetectPartFolder(node.RelativePath);
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            // Sidebar ordering rule for S02: keep Book.txt entries in their exact order,
            // then append that part folder's excluded projections immediately after the
            // last active entry in the same part section. Excluded files for folders with
            // no current Part divider are appended after all active Book.txt entries.
            if (partFolders.Contains(folder)
                && lastIndexByPartFolder.TryGetValue(folder, out var lastIndex)
                && lastIndex == index
                && groupedExcluded.Remove(folder, out var groupedNodes))
            {
                merged.AddRange(groupedNodes);
            }
        }

        foreach (var remaining in groupedExcluded
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && !partFolders.Contains(group.Key))
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            merged.AddRange(remaining.Value);
        }

        if (groupedExcluded.TryGetValue(string.Empty, out var rootLevelExcluded))
            merged.AddRange(rootLevelExcluded);

        return merged
            .Select((node, index) => node with { Index = index })
            .ToList()
            .AsReadOnly();
    }

    private static string TryGetRegistryUuid(
        IReadOnlyDictionary<string, ChapterRegistryEntry> registry,
        string relativePath)
    {
        foreach (var (uuid, entry) in registry)
        {
            if (string.Equals(entry.CurrentPath, relativePath, StringComparison.OrdinalIgnoreCase))
                return uuid;
        }

        return string.Empty;
    }

    private static string? DetectPartFolder(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.IndexOf('/');
        return slashIndex <= 0 ? null : normalized[..slashIndex];
    }

    // ── Word count totals ──────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the book total word count from all known chapter counts,
    /// then recalculates per-Part subtotals. Must be called on the UI thread.
    /// </summary>
    private void UpdateTotals()
    {
        int total = 0;
        ChapterViewModel? currentPart = null;
        int accumulated = 0;
        var partStatuses = new Dictionary<ChapterStatus, int>();
        var bookStatuses = new Dictionary<ChapterStatus, int>();

        foreach (var vm in _nodes)
        {
            if (vm.Node.Kind == NodeKind.Part)
            {
                if (currentPart != null)
                {
                    currentPart.PartTotalWordCount = accumulated;
                    currentPart.PartStatusSummary = ToStatusList(partStatuses);
                }

                currentPart = vm;
                accumulated = 0;
                partStatuses = new Dictionary<ChapterStatus, int>();
            }
            else if (vm.Node.Kind == NodeKind.Chapter && !vm.Node.IsExcluded)
            {
                total += vm.WordCount;
                accumulated += vm.WordCount;
                var status = Profile.DisplayStatus(vm.Status);
                bookStatuses[status] = bookStatuses.GetValueOrDefault(status) + 1;
                partStatuses[status] = partStatuses.GetValueOrDefault(status) + 1;
            }
        }

        if (currentPart != null)
        {
            currentPart.PartTotalWordCount = accumulated;
            currentPart.PartStatusSummary = ToStatusList(partStatuses);
        }

        TotalWordCount = total;
        BookStatusSummary = ToStatusList(bookStatuses);
    }

    /// <summary>Counts are keyed by display status; each bucket carries the profile label.</summary>
    private IReadOnlyList<StatusCount> ToStatusList(Dictionary<ChapterStatus, int> counts)
        => counts.Count == 0
            ? Array.Empty<StatusCount>()
            : counts.Select(kv => new StatusCount(kv.Key, kv.Value, Profile.LabelFor(kv.Key))).ToList();

    private void UpdateVisibility()
    {
        bool partExpanded = true;
        _visibleNodes.Clear();
        foreach (var vm in _nodes)
        {
            if (vm.Node.Kind == NodeKind.Part)
            {
                partExpanded = vm.IsExpanded;
                _visibleNodes.Add(vm);
            }
            else if (partExpanded)
            {
                _visibleNodes.Add(vm);
            }
        }
    }

    private void RebuildNodeLookup()
    {
        _nodesByPath.Clear();
        foreach (var vm in _nodes)
            _nodesByPath[vm.Node.RelativePath] = vm;
    }

    internal void OpenTargetFlyout(ChapterViewModel chapter)
    {
        if (chapter.Node.Kind == NodeKind.Part && !Profile.AllowsPartTargets)
            return;

        if (TargetFlyoutChapter != null && !ReferenceEquals(TargetFlyoutChapter, chapter))
            TargetFlyoutChapter.IsTargetFlyoutOpen = false;

        TargetFlyoutChapter = chapter;
        chapter.IsTargetFlyoutOpen = true;
        this.RaisePropertyChanged(nameof(IsTargetFlyoutOpen));
    }

    internal void CloseTargetFlyout(ChapterViewModel? chapter = null)
    {
        var target = chapter ?? TargetFlyoutChapter;
        if (target != null)
            target.IsTargetFlyoutOpen = false;

        if (ReferenceEquals(TargetFlyoutChapter, target))
            TargetFlyoutChapter = null;

        this.RaisePropertyChanged(nameof(IsTargetFlyoutOpen));
    }

    private void SetTargetFlyoutChapter(ChapterViewModel? chapter)
    {
        _targetFlyoutSubscription?.Dispose();
        _targetFlyoutSubscription = null;

        this.RaiseAndSetIfChanged(ref _targetFlyoutChapter, chapter, nameof(TargetFlyoutChapter));

        if (chapter != null)
        {
            _targetFlyoutSubscription = chapter
                .WhenAnyValue(x => x.IsTargetFlyoutOpen)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(IsTargetFlyoutOpen)));
        }

        this.RaisePropertyChanged(nameof(IsTargetFlyoutOpen));
    }

    public async Task RestorePaneSettingsAsync()
    {
        try
        {
            var stored = await _settingsStore.GetAsync<bool?>("chaptersPaneVisible").ConfigureAwait(false);
            if (stored == null)
                stored = await _settingsStore.GetAsync<bool?>("sidebarExpanded").ConfigureAwait(false);

            _isChaptersPaneVisible = stored ?? true;
            this.RaisePropertyChanged(nameof(IsChaptersPaneVisible));
        }
        catch
        {
            IsChaptersPaneVisible = true;
        }
    }

    private static bool RegistryChanged(
        IReadOnlyDictionary<string, ChapterRegistryEntry> before,
        IReadOnlyDictionary<string, ChapterRegistryEntry> after)
    {
        if (before.Count != after.Count)
            return true;

        foreach (var (uuid, entry) in before)
        {
            if (!after.TryGetValue(uuid, out var current))
                return true;

            if (!string.Equals(entry.Uuid, current.Uuid, StringComparison.Ordinal) ||
                !string.Equals(entry.CurrentPath, current.CurrentPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.Title, current.Title, StringComparison.OrdinalIgnoreCase) ||
                entry.Orphaned != current.Orphaned)
            {
                return true;
            }
        }

        return false;
    }

    public int GetBookEntryCount() => _nodes.Count(vm => !vm.Node.IsExcluded);

    public async Task<string?> PickManuscriptFileAsync()
    {
        if (string.IsNullOrWhiteSpace(ManuscriptRoot))
            return null;

        return await _filePicker.PickFileAsync(ManuscriptRoot).ConfigureAwait(false);
    }

    public string ToManuscriptRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(ManuscriptRoot))
            return absolutePath;

        var manuscriptRoot = Path.GetFullPath(ManuscriptRoot);
        var fullPath = Path.GetFullPath(absolutePath);
        if (!fullPath.StartsWith(manuscriptRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, manuscriptRoot, StringComparison.OrdinalIgnoreCase))
        {
            return absolutePath.Replace('\\', '/');
        }

        return Path.GetRelativePath(manuscriptRoot, fullPath).Replace('\\', '/');
    }

    private async Task CreateChapterAsync(CreateChapterRequest request)
    {
        await ExecuteStructuralOperationAsync(
            "Create chapter",
            request.ChapterPath,
            () => _structureService.CreateNewChapterAsync(BookTxtPath, request.ChapterPath, request.Content, request.Index))
            .ConfigureAwait(false);
    }

    private async Task CreatePartAsync(CreatePartRequest request)
    {
        await ExecuteStructuralOperationAsync(
            "Create part",
            request.PartPath,
            () => _structureService.CreateNewPartAsync(BookTxtPath, request.PartPath, request.Title, request.Index))
            .ConfigureAwait(false);
    }

    private async Task IncludeExistingFileAsync(IncludeExistingChapterRequest request)
    {
        Func<Task<Result<Unit>>> action;

        if (!string.IsNullOrWhiteSpace(request.PartPath))
        {
            action = () => _structureService.IncludeExistingEntryAfterPartAsync(
                BookTxtPath,
                request.ChapterPath,
                request.PartPath!);
        }
        else if (request.Index.HasValue)
        {
            action = () => _structureService.IncludeExistingEntryAsync(
                BookTxtPath,
                request.ChapterPath,
                request.Index.Value);
        }
        else
        {
            _notificationService.ShowError("Include file requires either an index or a part path.");
            return;
        }

        await ExecuteStructuralOperationAsync("Include file", request.ChapterPath, action).ConfigureAwait(false);
    }

    private async Task IncludeExcludedChapterAsync(string chapterPath)
    {
        if (!TryBuildIncludeExistingRequest(chapterPath, out var request, out var error))
        {
            _notificationService.ShowError(error ?? $"Include file for '{chapterPath}' in '{BookTxtPath}': unable to resolve insertion target.");
            return;
        }

        await IncludeExistingFileAsync(request!).ConfigureAwait(false);
    }

    private async Task RemoveFromBookAsync(string chapterPath)
    {
        if (_model == null) return;

        using var _ = _manuscriptService.SuppressFileWatcher();

        var result = await _structureService.ExcludeEntryAsync(_model.BookTxtPath, chapterPath).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError($"Failed to remove chapter: {result.Error}");
            return;
        }
        await ReloadWorkspaceAsync(_model.WorkspaceRoot, _model.BookTxtPath, reselectBook: false).ConfigureAwait(false);
    }

    private async Task RemoveMissingEntryAsync(string chapterPath)
    {
        await ExecuteStructuralOperationAsync(
            "Remove chapter",
            chapterPath,
            () => _structureService.RemoveEntryAsync(BookTxtPath, chapterPath)).ConfigureAwait(false);
    }

    private async Task RenameChapterAsync(RenameChapterRequest request)
    {
        if (!TryBuildChapterRenameTarget(request, out var replacementPath, out var error))
        {
            _notificationService.ShowError(error!);
            return;
        }

        await ExecuteRenameOperationAsync(
            "Rename chapter",
            request.ExistingPath,
            replacementPath!,
            () => _structureService.RenameEntryAsync(BookTxtPath, request.ExistingPath, replacementPath!))
            .ConfigureAwait(false);
    }

    private async Task RenamePartAsync(RenamePartRequest request)
    {
        if (!TryBuildPartRenameTarget(request, out var replacementPath, out var error))
        {
            _notificationService.ShowError(error!);
            return;
        }

        await ExecuteRenameOperationAsync(
            "Rename part",
            request.ExistingPath,
            replacementPath!,
            () => _structureService.RenameEntryAsync(BookTxtPath, request.ExistingPath, replacementPath!))
            .ConfigureAwait(false);
    }

    private bool TryBuildChapterRenameTarget(
        RenameChapterRequest request,
        out string? replacementPath,
        out string? error)
    {
        replacementPath = null;
        error = null;

        if (!TryResolveRenameSource("Rename chapter", request.ExistingPath, NodeKind.Chapter, out var node, out error))
            return false;

        if (!TryBuildSafePathSegment(request.NewTitle, "chapter title", out var segment, out error))
        {
            error = $"Rename chapter for '{request.ExistingPath}' in '{BookTxtPath}': {error}";
            return false;
        }

        var extension = Path.GetExtension(node!.RelativePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".md";

        var directory = Path.GetDirectoryName(node.RelativePath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        var fileName = segment + extension;
        replacementPath = string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory}/{fileName}";
        return true;
    }

    private bool TryBuildPartRenameTarget(
        RenamePartRequest request,
        out string? replacementPath,
        out string? error)
    {
        replacementPath = null;
        error = null;

        if (!TryResolveRenameSource("Rename part", request.ExistingPath, NodeKind.Part, out var node, out error))
            return false;

        if (!TryBuildSafePathSegment(request.NewTitle, "part title", out var segment, out error))
        {
            error = $"Rename part for '{request.ExistingPath}' in '{BookTxtPath}': {error}";
            return false;
        }

        var fileName = Path.GetFileName(node!.RelativePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = $"Rename part for '{request.ExistingPath}' in '{BookTxtPath}': Part path has no file name.";
            return false;
        }

        // Sidebar Part renames intentionally rename only the containing Part folder.
        // The Part markdown filename is preserved because BookTxtStructureService treats
        // the folder as the rename unit and rejects Part file-name changes.
        var parent = Path.GetDirectoryName(node.RelativePath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(parent))
        {
            error = $"Rename part for '{request.ExistingPath}' in '{BookTxtPath}': Part entries must be inside a folder.";
            return false;
        }

        var grandParent = Path.GetDirectoryName(parent.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        var replacementFolder = string.IsNullOrWhiteSpace(grandParent) ? segment : $"{grandParent}/{segment}";
        replacementPath = $"{replacementFolder}/{fileName}";
        return true;
    }

    private bool TryResolveRenameSource(
        string operation,
        string existingPath,
        NodeKind expectedKind,
        out ChapterNode? node,
        out string? error)
    {
        node = null;

        if (!HasWorkspace || string.IsNullOrWhiteSpace(BookTxtPath))
        {
            error = "No workspace is open.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(existingPath))
        {
            error = $"{operation} in '{BookTxtPath}': source path is required.";
            return false;
        }

        if (!_nodesByPath.TryGetValue(existingPath, out var vm))
        {
            error = $"{operation} for '{existingPath}' in '{BookTxtPath}': source node was not found in the sidebar.";
            return false;
        }

        node = vm.Node;
        if (node.Kind != expectedKind)
        {
            error = $"{operation} for '{existingPath}' in '{BookTxtPath}': source node is a {node.Kind}, not a {expectedKind}.";
            return false;
        }

        if (node.IsExcluded)
        {
            error = $"{operation} for '{existingPath}' in '{BookTxtPath}': excluded nodes cannot be renamed from the sidebar.";
            return false;
        }

        if (node.IsMissing)
        {
            error = $"{operation} for '{existingPath}' in '{BookTxtPath}': missing nodes cannot be renamed.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryBuildSafePathSegment(string title, string fieldName, out string segment, out string? error)
    {
        segment = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(title))
        {
            error = $"{fieldName} is required.";
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(title.Length);
        var previousWasSeparator = false;

        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSeparator = false;
            }
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_' || invalid.Contains(ch))
            {
                if (!previousWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    previousWasSeparator = true;
                }
            }
            else
            {
                if (!previousWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    previousWasSeparator = true;
                }
            }
        }

        segment = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(segment))
        {
            error = $"{fieldName} must contain at least one letter or number.";
            return false;
        }

        return true;
    }

    private bool TryBuildIncludeExistingRequest(
        string chapterPath,
        out IncludeExistingChapterRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!_nodesByPath.TryGetValue(chapterPath, out var chapter))
        {
            error = $"Include file for '{chapterPath}' in '{BookTxtPath}': chapter was not found in the sidebar.";
            return false;
        }

        if (chapter.Node.Kind != NodeKind.Chapter)
        {
            error = $"Include file for '{chapterPath}' in '{BookTxtPath}': only chapters can be included.";
            return false;
        }

        if (chapter.Node.IsMissing)
        {
            error = $"Include file for '{chapterPath}' in '{BookTxtPath}': missing files cannot be included.";
            return false;
        }

        if (!chapter.Node.IsExcluded)
        {
            error = $"Include file for '{chapterPath}' in '{BookTxtPath}': chapter is already included.";
            return false;
        }

        var index = 0;
        foreach (var node in _nodes)
        {
            if (string.Equals(node.Node.RelativePath, chapterPath, StringComparison.OrdinalIgnoreCase))
                break;

            if (!node.Node.IsExcluded)
                index++;
        }

        request = new IncludeExistingChapterRequest(chapterPath, Index: index);
        return true;
    }

    private async Task ReorderChapterAsync(ReorderCardRequest request)
    {
        if (_model == null) return;

        if (!TryResolveSidebarReorderIndex(request, out var newIndex, out var error))
        {
            _notificationService.ShowError($"Reorder failed for '{request.RelativePath}' in '{BookTxtPath}': {error}");
            return;
        }

        try
        {
            using var _ = _manuscriptService.SuppressFileWatcher();

            var result = await _structureService.ReorderEntryAsync(_model.BookTxtPath, request.RelativePath, newIndex)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _notificationService.ShowError($"Failed to reorder sidebar entry '{request.RelativePath}' in '{BookTxtPath}' to index {newIndex}: {result.Error}");
                return;
            }

            var reloadResult = await ReloadCurrentWorkspaceAsync().ConfigureAwait(false);
            if (!reloadResult.IsSuccess)
                _notificationService.ShowError($"Failed to sync sidebar order after reordering '{request.RelativePath}' in '{BookTxtPath}': {reloadResult.Error}");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Reorder sidebar entry '{request.RelativePath}' in '{BookTxtPath}' failed: {ex.Message}");
        }
    }

    private bool TryResolveSidebarReorderIndex(ReorderCardRequest request, out int newIndex, out string? error)
    {
        newIndex = 0;
        error = null;

        var activeNodes = _nodes
            .Where(node => !node.Node.IsExcluded && !node.Node.IsMissing)
            .ToList();

        var sourceIndex = activeNodes.FindIndex(node =>
            string.Equals(node.Node.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (sourceIndex < 0)
        {
            if (_nodesByPath.TryGetValue(request.RelativePath, out var projectedSource))
            {
                if (projectedSource.Node.IsExcluded)
                    error = $"Source '{request.RelativePath}' is excluded and cannot be reordered.";
                else if (projectedSource.Node.IsMissing)
                    error = $"Source '{request.RelativePath}' is missing and cannot be reordered.";
                else
                    error = $"Source '{request.RelativePath}' is not an active Book.txt entry.";
            }
            else
            {
                error = $"Source '{request.RelativePath}' was not found in the sidebar.";
            }

            return false;
        }

        var source = activeNodes[sourceIndex];
        var targetResolution = ResolveSidebarReorderTarget(request, activeNodes, sourceIndex);
        if (!targetResolution.IsSuccess)
        {
            error = targetResolution.Error;
            return false;
        }

        var targetIndex = targetResolution.Value!.TargetIndex;
        var target = activeNodes[targetIndex];

        if (target.Node.IsExcluded || target.Node.IsMissing)
        {
            error = $"Target '{target.Node.RelativePath}' is not an active Book.txt entry.";
            return false;
        }

        if (source.Node.Kind == NodeKind.Part)
        {
            if (target.Node.Kind != NodeKind.Part)
            {
                error = $"Part '{source.Node.RelativePath}' can only be dropped on another Part divider, not chapter '{target.Node.RelativePath}'.";
                return false;
            }

            if (targetResolution.Value.DropAfter)
            {
                var nextPartIndex = FindNextPartIndex(activeNodes, targetIndex + 1);
                if (nextPartIndex < 0)
                {
                    error = $"Part '{source.Node.RelativePath}' cannot be dropped after last Part '{target.Node.RelativePath}' because Book.txt part block moves require a following Part target.";
                    return false;
                }

                newIndex = nextPartIndex;
            }
            else
            {
                newIndex = targetIndex;
            }

            return true;
        }

        if (target.Node.Kind != NodeKind.Chapter)
        {
            error = $"Chapter '{source.Node.RelativePath}' can only be dropped on another chapter in the same Part section, not Part '{target.Node.RelativePath}'.";
            return false;
        }

        var sourcePart = FindContainingPartPath(activeNodes, sourceIndex);
        var targetPart = FindContainingPartPath(activeNodes, targetIndex);
        if (!string.Equals(sourcePart, targetPart, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Chapter '{source.Node.RelativePath}' cannot be moved across Part sections from '{DisplayPartSection(sourcePart)}' to '{DisplayPartSection(targetPart)}'. Move chapters between Parts from the corkboard.";
            return false;
        }

        newIndex = targetResolution.Value.DropAfter
            ? (sourceIndex > targetIndex ? targetIndex + 1 : targetIndex)
            : (sourceIndex < targetIndex ? targetIndex - 1 : targetIndex);
        return true;
    }

    private static Result<SidebarReorderTarget> ResolveSidebarReorderTarget(
        ReorderCardRequest request,
        IReadOnlyList<ChapterViewModel> activeNodes,
        int sourceIndex)
    {
        if (request.NewIndex.HasValue)
        {
            var targetIndex = request.NewIndex.Value;
            if (targetIndex < 0 || targetIndex >= activeNodes.Count)
                return Result<SidebarReorderTarget>.Fail($"Target index {targetIndex} is outside the active Book.txt entry range.");

            return Result<SidebarReorderTarget>.Ok(new SidebarReorderTarget(targetIndex, DropAfter: false));
        }

        if (!string.IsNullOrWhiteSpace(request.AfterRelativePath))
        {
            var afterIndex = FindActiveNodeIndex(activeNodes, request.AfterRelativePath);
            if (afterIndex < 0)
                return Result<SidebarReorderTarget>.Fail($"Target '{request.AfterRelativePath}' was not found as an active Book.txt entry.");

            return Result<SidebarReorderTarget>.Ok(new SidebarReorderTarget(afterIndex, DropAfter: true));
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeRelativePath))
        {
            var beforeIndex = FindActiveNodeIndex(activeNodes, request.BeforeRelativePath);
            if (beforeIndex < 0)
                return Result<SidebarReorderTarget>.Fail($"Target '{request.BeforeRelativePath}' was not found as an active Book.txt entry.");

            return Result<SidebarReorderTarget>.Ok(new SidebarReorderTarget(beforeIndex, DropAfter: false));
        }

        return Result<SidebarReorderTarget>.Fail("Reorder requires either a target index or a neighbor path.");
    }

    private static int FindActiveNodeIndex(IReadOnlyList<ChapterViewModel> activeNodes, string relativePath)
    {
        for (var i = 0; i < activeNodes.Count; i++)
        {
            if (string.Equals(activeNodes[i].Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindNextPartIndex(IReadOnlyList<ChapterViewModel> activeNodes, int startIndex)
    {
        for (var i = startIndex; i < activeNodes.Count; i++)
        {
            if (activeNodes[i].Node.Kind == NodeKind.Part)
                return i;
        }

        return -1;
    }

    private static string? FindContainingPartPath(IReadOnlyList<ChapterViewModel> activeNodes, int nodeIndex)
    {
        string? currentPart = null;
        for (var i = 0; i <= nodeIndex && i < activeNodes.Count; i++)
        {
            if (activeNodes[i].Node.Kind == NodeKind.Part)
                currentPart = activeNodes[i].Node.RelativePath;
        }

        return currentPart;
    }

    private static string DisplayPartSection(string? partPath) =>
        string.IsNullOrWhiteSpace(partPath) ? "the root section" : partPath;

    private sealed record SidebarReorderTarget(int TargetIndex, bool DropAfter);

    private async Task ExecuteRenameOperationAsync(
        string operation,
        string sourcePath,
        string replacementPath,
        Func<Task<Result<Unit>>> action)
    {
        if (!HasWorkspace || string.IsNullOrWhiteSpace(BookTxtPath) || _model == null)
        {
            _notificationService.ShowError("No workspace is open.");
            return;
        }

        try
        {
            using var suppressFileWatcher = _manuscriptService.SuppressFileWatcher();

            var result = await action().ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _notificationService.ShowError(
                    $"{operation} for '{sourcePath}' to '{replacementPath}' in '{BookTxtPath}': {result.Error}");
                return;
            }

            var reloadResult = await ReloadCurrentWorkspaceAsync().ConfigureAwait(false);
            if (!reloadResult.IsSuccess)
            {
                _notificationService.ShowError(
                    $"Failed to reload workspace after {operation.ToLowerInvariant()} for '{sourcePath}' to '{replacementPath}' in '{BookTxtPath}': {reloadResult.Error}");
                return;
            }

            var hydrationTask = _hydrationTask;
            if (hydrationTask == null)
            {
                SelectReplacementNodeIfPresent(replacementPath);
            }
            else
            {
                _ = hydrationTask.ContinueWith(task =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectReplacementNodeIfPresent(replacementPath)),
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                $"{operation} for '{sourcePath}' to '{replacementPath}' in '{BookTxtPath}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects (and so opens) the chapter whose relative path matches <paramref name="relativePath"/>,
    /// ignoring case. Returns false when no such chapter is loaded.
    /// </summary>
    public bool SelectChapterByPath(string relativePath)
    {
        var match = _nodes.FirstOrDefault(vm =>
            string.Equals(vm.Node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return false;

        SelectedNode = match;
        return true;
    }

    /// <summary>
    /// Pauses Book.txt change detection until the returned handle is disposed, so writes made
    /// by the app itself are not reported as external edits.
    /// </summary>
    public IDisposable SuppressFileWatcher() => _manuscriptService.SuppressFileWatcher();

    private void SelectReplacementNodeIfPresent(string replacementPath)
    {
        if (_nodesByPath.TryGetValue(replacementPath, out var replacementNode))
        {
            _isSwitching = true;
            SelectedNode = replacementNode;
            _isSwitching = false;
        }
    }

    private async Task ExecuteStructuralOperationAsync(
        string operation,
        string? path,
        Func<Task<Result<Unit>>> action)
    {
        if (!HasWorkspace || string.IsNullOrWhiteSpace(BookTxtPath))
        {
            _notificationService.ShowError("No workspace is open.");
            return;
        }

        try
        {
            using var _ = _manuscriptService.SuppressFileWatcher();

            var result = await action().ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                var target = string.IsNullOrWhiteSpace(path)
                    ? $"{operation} in '{BookTxtPath}'"
                    : $"{operation} for '{path}' in '{BookTxtPath}'";
                _notificationService.ShowError($"{target}: {result.Error}");
                return;
            }

            var reloadResult = await ReloadCurrentWorkspaceAsync().ConfigureAwait(false);
            if (!reloadResult.IsSuccess)
                _notificationService.ShowError($"Failed to reload workspace after {operation.ToLowerInvariant()}: {reloadResult.Error}");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"{operation} failed: {ex.Message}");
        }
    }

    private async Task PersistChaptersPaneVisibleAsync(bool value)
    {
        try
        {
            await _settingsStore.SetAsync("chaptersPaneVisible", value).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; layout preference may not persist across sessions if storage fails.
        }
    }

    private async Task PersistPartExpandedStatesAsync(string workspaceRoot, List<string> collapsedPaths)
    {
        try
        {
            var existing = await _settingsStore.GetAsync<Dictionary<string, List<string>>>("partCollapsedStates")
                .ConfigureAwait(false) ?? new();
            existing[workspaceRoot] = collapsedPaths;
            await _settingsStore.SetAsync("partCollapsedStates", existing).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; expand/collapse state may not persist across sessions if storage fails.
        }
    }
}
