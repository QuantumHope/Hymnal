using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Backs the F3 Chapter Info side-panel.
/// Mirrors the NotesViewModel lifecycle pattern:
///   • observes EditorViewModel.ActiveNode
///   • cancels in-flight saves on chapter switch
///   • ToggleCommand gated on ActiveNode != null
/// Adds status-change, date-edit, and target-edit commands that persist via
/// the workspace's <see cref="IStoryMetadataStore"/> and call <see cref="ChapterViewModel.ApplyPhaseData"/>
/// to re-sync the sidebar without re-loading the whole workspace.
/// </summary>
public sealed class ChapterInfoViewModel : ViewModelBase, IDisposable
{
    // ── Profile-driven view-model data (consumed by ChapterInfoView) ──────────

    /// <summary>Profile of the open workspace.</summary>
    public ProjectProfile Profile => _workspaceViewModel.Profile;

    /// <summary>Statuses visible in the current profile; bound to the status ComboBox ItemsSource.</summary>
    public IReadOnlyList<StatusOption> StatusOptions => Profile.VisibleStatuses;

    /// <summary>Panel heading for the current profile ("Chapter Info" / "Story Info").</summary>
    public string PanelTitle => Profile.InfoPanelTitle;

    /// <summary>View-menu header for the panel toggle, with access key.</summary>
    public string ToggleMenuHeader => $"Toggle _{PanelTitle}";

    /// <summary>Tooltip for the panel toggle buttons.</summary>
    public string ToggleToolTip => $"{PanelTitle} (F3)";
    // ── Injected services ────────────────────────────────────────────────────

    private readonly EditorViewModel _editorViewModel;
    private readonly WorkspaceViewModel _workspaceViewModel;
    private readonly IAppSettingsStore _settingsStore;
    private readonly INotificationService _notificationService;
    private readonly WordCountHistoryService _historyService;

    // ── Per-chapter state ────────────────────────────────────────────────────

    private ChapterViewModel? _activeChapterVm;
    private CancellationTokenSource _saveCts = new();
    /// <summary>Cancelled on Dispose, ending the "Saved" indicator delay.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetime;
    private string? _loadedUuid;

    /// <summary>Disposables that live only for the active chapter subscription.</summary>
    private CompositeDisposable _chapterDisposables = new();

    /// <summary>Suppresses auto-save during programmatic row sync to avoid spurious writes.</summary>
    private bool _suppressAutoSave;

    private readonly Subject<Unit> _scheduleSaveSubject = new();

    /// <summary>True from a schedule row edit until the next schedule save starts.</summary>
    private volatile bool _scheduleSavePending;

    /// <summary>Change subscriptions for the current <see cref="PhaseScheduleRows"/>.</summary>
    private readonly SerialDisposable _scheduleRowSubscriptions = new();

    // ── Public observable properties ─────────────────────────────────────────

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private string? _chapterTitle;
    public string? ChapterTitle
    {
        get => _chapterTitle;
        private set => this.RaiseAndSetIfChanged(ref _chapterTitle, value);
    }

    // ── Bindable display props that mirror the active ChapterViewModel ────────

    private ChapterStatus _status;
    public ChapterStatus Status
    {
        get => _status;
        private set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(DisplayStatus));
        }
    }

    /// <summary><see cref="Status"/> mapped onto a status visible in <see cref="Profile"/>.</summary>
    public ChapterStatus DisplayStatus => Profile.DisplayStatus(Status);

    private string? _phaseStartDate;
    /// <summary>ISO 8601 date string, e.g. "2025-06-01". Editable in the pane.</summary>
    public string? PhaseStartDate
    {
        get => _phaseStartDate;
        set => this.RaiseAndSetIfChanged(ref _phaseStartDate, value);
    }

    private string? _phaseEndDate;
    /// <summary>ISO 8601 date string. Editable in the pane.</summary>
    public string? PhaseEndDate
    {
        get => _phaseEndDate;
        set => this.RaiseAndSetIfChanged(ref _phaseEndDate, value);
    }

    /// <summary>
    /// One row per Gantt phase of the current profile (Novel: Outlining → Reviewing).
    /// Bound to the per-phase schedule table in ChapterInfoView. Edit in place; save via
    /// SaveScheduleCommand. Rebuilt when the workspace profile changes.
    /// </summary>
    public ObservableCollection<PhaseScheduleRowViewModel> PhaseScheduleRows { get; } = new();

    private int _wordCount;
    public int WordCount
    {
        get => _wordCount;
        private set => this.RaiseAndSetIfChanged(ref _wordCount, value);
    }

    private readonly ObservableAsPropertyHelper<string> _wordCountDisplay;
    public string WordCountDisplay => _wordCountDisplay.Value;

    private string? _targetDisplay;
    public string? TargetDisplay
    {
        get => _targetDisplay;
        private set => this.RaiseAndSetIfChanged(ref _targetDisplay, value);
    }

    private int? _pendingTarget;
    /// <summary>
    /// Editable target word count typed by the user; passed to SetTargetCommand on confirmation.
    /// </summary>
    public int? PendingTarget
    {
        get => _pendingTarget;
        set => this.RaiseAndSetIfChanged(ref _pendingTarget, value);
    }

    /// <summary>Story-length presets of the current profile (empty for Novel).</summary>
    public IReadOnlyList<TargetPreset> TargetPresets => Profile.TargetPresets;

    public bool HasTargetPresets => TargetPresets.Count > 0;

    private TargetPreset? _selectedPreset;
    /// <summary>Preset chosen in the Target section; a non-custom choice fills <see cref="PendingTarget"/>.</summary>
    public TargetPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPreset, value);
            if (value?.MinWords is { } min)
                PendingTarget = min;
        }
    }

    private void SyncSelectedPreset(WordCountTarget? target)
    {
        _selectedPreset = target?.Preset is { } key
            ? TargetPresets.FirstOrDefault(p => p.Key == key)
            : null;
        this.RaisePropertyChanged(nameof(SelectedPreset));
    }

    private double _proximityFill;
    public double ProximityFill
    {
        get => _proximityFill;
        private set => this.RaiseAndSetIfChanged(ref _proximityFill, value);
    }

    private bool _hasTarget;
    public bool HasTarget
    {
        get => _hasTarget;
        private set => this.RaiseAndSetIfChanged(ref _hasTarget, value);
    }

    private bool _scheduleSavedRecently;
    /// <summary>True for ~2 seconds after an auto-save completes; drives the "Saved" indicator.</summary>
    public bool ScheduleSavedRecently
    {
        get => _scheduleSavedRecently;
        private set => this.RaiseAndSetIfChanged(ref _scheduleSavedRecently, value);
    }

    private IReadOnlyList<HistoryChartPoint> _historyItems = Array.Empty<HistoryChartPoint>();
    public IReadOnlyList<HistoryChartPoint> HistoryItems
    {
        get => _historyItems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _historyItems, value);
            this.RaisePropertyChanged(nameof(HasHistoryItems));
        }
    }

    public bool HasHistoryItems => _historyItems.Count > 0;

    // ── PrefillPhaseDate ─────────────────────────────────────────────────────

    private bool _prefillPhaseDate = true;
    /// <summary>
    /// When true, SetStatusCommand auto-fills PhaseStartDate with today's date.
    /// Persisted via IAppSettingsStore key "prefillPhaseDate".
    /// </summary>
    public bool PrefillPhaseDate
    {
        get => _prefillPhaseDate;
        set
        {
            this.RaiseAndSetIfChanged(ref _prefillPhaseDate, value);
            _ = PersistPrefillPreferenceAsync(value);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Shows/hides the panel. Only active when a chapter is open.</summary>
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// <summary>
    /// Changes the chapter status and optionally pre-fills PhaseStartDate.
    /// Persists through the metadata store and re-syncs ChapterViewModel.
    /// </summary>
    public ReactiveCommand<ChapterStatus, Unit> SetStatusCommand { get; }

    /// <summary>
    /// Persists the current PhaseStartDate/PhaseEndDate string fields through the metadata store
    /// and re-syncs ChapterViewModel.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveDatesCommand { get; }

    /// <summary>
    /// Persists the per-phase schedule rows through the metadata store and re-syncs ChapterViewModel.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveScheduleCommand { get; }

    /// <summary>
    /// Sets (or clears when null) the word-count target via ChapterViewModel.SetTargetCommand.
    /// </summary>
    public ReactiveCommand<int?, Unit> SetTargetCommand { get; }

    // ── Constructor ──────────────────────────────────────────────────────────

    public ChapterInfoViewModel(
        EditorViewModel editorViewModel,
        WorkspaceViewModel workspaceViewModel,
        IAppSettingsStore settingsStore,
        INotificationService notificationService,
        WordCountHistoryService historyService)
    {
        _editorViewModel = editorViewModel;
        _workspaceViewModel = workspaceViewModel;
        _settingsStore = settingsStore;
        _notificationService = notificationService;
        _lifetime = _lifetimeCts.Token;
        _historyService = historyService;

        try
        {
            _isVisible = false;
        }
        catch
        {
            _isVisible = false;
        }

        // ── OAPHs that delegate to this VM's own observable props ────────────

        _wordCountDisplay = this
            .WhenAnyValue(x => x.WordCount)
            .Select(c => $"{c:N0} w")
            .ToProperty(this, x => x.WordCountDisplay, out _wordCountDisplay);
        Disposables.Add(_wordCountDisplay);

        // ── Toggle command: gate on active node ──────────────────────────────
        var hasActiveNode = editorViewModel
            .WhenAnyValue(x => x.ActiveNode)
            .Select(n => n != null);

        ToggleCommand = ReactiveCommand.Create(
            () =>
            {
                if (_loadedUuid != null)
                {
                    IsVisible = !IsVisible;
                    _ = PersistChapterInfoVisibilityAsync(IsVisible);
                }
            },
            canExecute: hasActiveNode);
        Disposables.Add(
            ToggleCommand.ThrownExceptions
                .Subscribe(ex => notificationService.ShowError($"Toggle chapter info failed: {ex.Message}")));

        // ── SetStatusCommand ─────────────────────────────────────────────────
        SetStatusCommand = ReactiveCommand.CreateFromTask<ChapterStatus>(SetStatusAsync);
        Disposables.Add(
            SetStatusCommand.ThrownExceptions
                .Subscribe(ex => notificationService.ShowError($"Set status failed: {ex.Message}")));

        // ── SaveDatesCommand ─────────────────────────────────────────────────
        SaveDatesCommand = ReactiveCommand.CreateFromTask(SaveDatesAsync);
        Disposables.Add(
            SaveDatesCommand.ThrownExceptions
                .Subscribe(ex => notificationService.ShowError($"Save dates failed: {ex.Message}")));

        // ── SaveScheduleCommand ──────────────────────────────────────────────
        SaveScheduleCommand = ReactiveCommand.CreateFromTask(SaveScheduleAsync);
        Disposables.Add(
            SaveScheduleCommand.ThrownExceptions
                .Subscribe(ex => notificationService.ShowError($"Save schedule failed: {ex.Message}")));

        // ── SetTargetCommand ─────────────────────────────────────────────────
        SetTargetCommand = ReactiveCommand.CreateFromTask<int?>(SetTargetAsync);
        Disposables.Add(
            SetTargetCommand.ThrownExceptions
                .Subscribe(ex => notificationService.ShowError($"Set target failed: {ex.Message}")));

        // ── Auto-save debounce for the phase schedule rows ───────────────────
        Disposables.Add(
            _scheduleSaveSubject
                .Throttle(TimeSpan.FromMilliseconds(800), TaskPoolScheduler.Default)
                .Subscribe(unit => _ = AutoSaveScheduleAsync()));

        Disposables.Add(_scheduleRowSubscriptions);
        RebuildScheduleRows();

        // ── Rebuild profile-driven state when the workspace profile changes ──
        Disposables.Add(
            workspaceViewModel
                .WhenAnyValue(x => x.Profile)
                .Skip(1)
                .Subscribe(_ => OnProfileChanged()));

        // ── Observe active node ──────────────────────────────────────────────
        Disposables.Add(
            editorViewModel
                .WhenAnyValue(x => x.ActiveNode)
                .Subscribe(node => OnActiveNodeChanged(node)));

        // ── Re-attach to the rebuilt ChapterViewModel when the workspace nodes are replaced ──
        var nodes = (INotifyCollectionChanged)workspaceViewModel.Nodes;
        nodes.CollectionChanged += OnWorkspaceNodesChanged;
        Disposables.Add(Disposable.Create(() => nodes.CollectionChanged -= OnWorkspaceNodesChanged));

        // Prefill preference restored during RestoreSettingsAsync().
    }

    // ── Active-node transition handler ───────────────────────────────────────

    /// <summary>
    /// Workspace reloads (e.g. a project-kind switch) dispose and replace every ChapterViewModel
    /// while the editor's active file stays open. Re-resolve the active chapter so the panel never
    /// keeps a disposed view-model that carries the old profile.
    /// </summary>
    private void OnWorkspaceNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var node = _editorViewModel.ActiveNode;
        if (node == null)
            return;

        var current = _workspaceViewModel.Nodes
            .FirstOrDefault(vm => vm.Node.RelativePath == node.RelativePath);
        if (ReferenceEquals(current, _activeChapterVm))
            return;
        if (current == null && _activeChapterVm != null && _workspaceViewModel.Nodes.Contains(_activeChapterVm))
            return;

        // Same chapter, new view-model: no outgoing edits to flush.
        OnActiveNodeChanged(node, flushPendingSchedule: false);
    }

    private void OnActiveNodeChanged(ChapterNode? node) => OnActiveNodeChanged(node, flushPendingSchedule: true);

    private void OnActiveNodeChanged(ChapterNode? node, bool flushPendingSchedule)
    {
        // Flush any pending schedule edits for the outgoing chapter before switching.
        if (flushPendingSchedule && _loadedUuid != null && !_suppressAutoSave)
            _ = SaveScheduleAsync();

        // Cancel any pending in-flight operation for the previous chapter.
        _saveCts.Cancel();
        _saveCts.Dispose();
        _saveCts = new CancellationTokenSource();

        // Dispose per-chapter reactive subscriptions.
        _chapterDisposables.Dispose();
        _chapterDisposables = new CompositeDisposable();

        if (node == null)
        {
            _loadedUuid = null;
            _activeChapterVm = null;
            ChapterTitle = null;
            Status = ChapterStatus.Outlining;
            PhaseStartDate = null;
            PhaseEndDate = null;
            WordCount = 0;
            TargetDisplay = null;
            HasTarget = false;
            ProximityFill = 0.0;
            IsVisible = false;
            return;
        }

        var wasVisible = IsVisible;
        ChapterTitle = node.Title;

        // Find the ChapterViewModel for this node.
        var chapterVm = _workspaceViewModel.Nodes
            .FirstOrDefault(vm => vm.Node.RelativePath == node.RelativePath);

        _activeChapterVm = chapterVm;
        _loadedUuid = chapterVm?.Uuid;

        if (chapterVm != null)
        {
            // Sync display state from the ChapterViewModel immediately.
            SyncFromChapterVm(chapterVm);

            if (!string.IsNullOrEmpty(_loadedUuid))
                _ = RecordHistoryAndReloadAsync(_loadedUuid, chapterVm.WordCount);

            // Subscribe to live word count, status, and target changes.
            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.WordCount)
                    .Subscribe(c => WordCount = c));

            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.Status)
                    .Subscribe(s => Status = s));

            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.PhaseData)
                    .Subscribe(pd =>
                    {
                        PhaseStartDate = pd?.PhaseStartDate;
                        PhaseEndDate = pd?.PhaseEndDate;
                        SyncScheduleRows(pd);
                    }));

            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.Target)
                    .Subscribe(t =>
                    {
                        TargetDisplay = FormatTargetDisplay(t, Profile);
                        SyncSelectedPreset(t);
                    }));

            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.HasTarget)
                    .Subscribe(h => HasTarget = h));

            _chapterDisposables.Add(
                chapterVm.WhenAnyValue(x => x.ProximityFill)
                    .Subscribe(f => ProximityFill = f));

            IsVisible = wasVisible;
        }
        else
        {
            IsVisible = wasVisible;
        }
    }

    private async Task RecordHistoryAndReloadAsync(string uuid, int wordCount)
    {
        var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
        if (!string.IsNullOrEmpty(workspaceRoot) && wordCount > 0)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                await _historyService.AppendAsync(workspaceRoot, uuid, today, wordCount).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal; chart will still reload from whatever data exists.
            }
        }

        await LoadChapterHistoryAsync(uuid).ConfigureAwait(false);
    }

    private async Task LoadChapterHistoryAsync(string uuid)
    {
        var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            HistoryItems = Array.Empty<HistoryChartPoint>();
            return;
        }

        try
        {
            var all = await _historyService.GetAllAsync(workspaceRoot).ConfigureAwait(false);
            var points = all
                .Where(e => string.Equals(e.Uuid, uuid, StringComparison.Ordinal))
                .Select(e => new HistoryChartPoint(e.Date, e.WordCount))
                .OrderBy(p => p.Date)
                .ToList();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                HistoryItems = points);
        }
        catch
        {
            // Non-fatal; chart stays empty
        }
    }

    private void SyncFromChapterVm(ChapterViewModel vm)
    {
        Status = vm.Status;
        PhaseStartDate = vm.PhaseData?.PhaseStartDate;
        PhaseEndDate = vm.PhaseData?.PhaseEndDate;
        SyncScheduleRows(vm.PhaseData);
        WordCount = vm.WordCount;
        TargetDisplay = FormatTargetDisplay(vm.Target, Profile);
        SyncSelectedPreset(vm.Target);
        HasTarget = vm.HasTarget;
        ProximityFill = vm.ProximityFill;
    }

    /// <summary>Syncs <see cref="PhaseScheduleRows"/> from the supplied <see cref="PhaseData"/>.</summary>
    private void SyncScheduleRows(PhaseData? pd)
    {
        _suppressAutoSave = true;
        try
        {
            foreach (var row in PhaseScheduleRows)
            {
                // Read raw strings directly so partial/in-progress edits survive the round-trip.
                PhaseScheduleEntry? entry = null;
                pd?.Schedule?.TryGetValue(row.PhaseName, out entry);

                string? startDate = entry?.StartDate;
                string? endDate   = entry?.EndDate;

                // Fall back to legacy fields for the currently-active phase.
                if (startDate == null && endDate == null && pd?.Status.ToString() == row.PhaseName)
                {
                    startDate = pd?.PhaseStartDate;
                    endDate   = pd?.PhaseEndDate;
                }

                row.StartDate = startDate;
                row.EndDate   = endDate;
                var prog = entry?.Progress;
                row.Progress  = prog > 0 ? prog : null;
            }
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void OnProfileChanged()
    {
        this.RaisePropertyChanged(nameof(Profile));
        this.RaisePropertyChanged(nameof(StatusOptions));
        this.RaisePropertyChanged(nameof(PanelTitle));
        this.RaisePropertyChanged(nameof(TargetPresets));
        this.RaisePropertyChanged(nameof(HasTargetPresets));
        this.RaisePropertyChanged(nameof(ToggleMenuHeader));
        this.RaisePropertyChanged(nameof(ToggleToolTip));
        this.RaisePropertyChanged(nameof(DisplayStatus));
        RebuildScheduleRows();
        SyncScheduleRows(_activeChapterVm?.PhaseData);
        if (_activeChapterVm != null)
            TargetDisplay = FormatTargetDisplay(_activeChapterVm.Target, Profile);
    }

    /// <summary>Recreates <see cref="PhaseScheduleRows"/> from the current profile's Gantt phases.</summary>
    private void RebuildScheduleRows()
    {
        PhaseScheduleRows.Clear();
        foreach (var name in Profile.GanttPhases)
            PhaseScheduleRows.Add(new PhaseScheduleRowViewModel(name));

        var subscriptions = new CompositeDisposable();
        foreach (var row in PhaseScheduleRows)
        {
            subscriptions.Add(
                row.WhenAnyValue(x => x.StartDate, x => x.EndDate, x => x.Progress)
                    .Skip(1)
                    .Subscribe(_ =>
                    {
                        if (!_suppressAutoSave)
                        {
                            _scheduleSavePending = true;
                            _scheduleSaveSubject.OnNext(Unit.Default);
                        }
                    }));
        }
        _scheduleRowSubscriptions.Disposable = subscriptions;
    }

    /// <summary>
    /// Fires after the debounce throttle; saves the schedule and briefly shows the "Saved" indicator.
    /// Never throws, so fire-and-forget callers are safe.
    /// </summary>
    private async Task AutoSaveScheduleAsync()
    {
        try
        {
            await SaveScheduleAsync().ConfigureAwait(false);

            // Post never throws once the app is shutting down; InvokeAsync would, killing the process.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ScheduleSavedRecently = true);
            await Task.Delay(2000, _lifetime).ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ScheduleSavedRecently = false);
        }
        catch (OperationCanceledException)
        {
            // Disposed during the delay.
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _notificationService.ShowError($"Schedule save failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Saves schedule edits still waiting on the debounce now. True when nothing was pending or the save
    /// succeeded; false (after showing the error) when it failed.
    /// </summary>
    public async Task<bool> FlushScheduleAsync()
    {
        if (!_scheduleSavePending)
            return true;
        try
        {
            await SaveScheduleAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _scheduleSavePending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _notificationService.ShowError($"Schedule save failed: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Triggers an immediate (non-debounced) save; called from view LostFocus handler.</summary>
    public void RequestImmediateScheduleSave()
    {
        if (!_suppressAutoSave)
            _ = AutoSaveScheduleAsync();
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task SetStatusAsync(ChapterStatus newStatus)
    {
        var chapterVm = _activeChapterVm;
        var uuid = _loadedUuid;
        var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
        var profile = Profile;
        var phases = profile.GanttPhases;

        if (chapterVm == null || string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(workspaceRoot))
            return;

        PhaseData? updated = null;
        await _workspaceViewModel.MetadataStore.UpsertPhaseDataAsync(workspaceRoot, chapterVm.Story, current =>
        {
            var statusChanged = current?.Status != newStatus;
            var isAdvancing = (int)newStatus > (int)(current?.Status ?? ChapterStatus.Planned);

            // Preserve the existing per-phase schedule; never wipe it on a status change.
            var schedule = current?.Schedule != null
                ? new Dictionary<string, PhaseScheduleEntry>(current.Schedule)
                : new Dictionary<string, PhaseScheduleEntry>();

            if (_prefillPhaseDate && statusChanged)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                // Close out the previous phase when advancing forward.
                if (isAdvancing)
                {
                    // Close the segment the old status displays as (story Polishing → Editing).
                    var oldPhaseName = current is null ? null : profile.DisplayStatus(current.Status).ToString();
                    if (oldPhaseName != null && phases.Contains(oldPhaseName))
                    {
                        schedule.TryGetValue(oldPhaseName, out var old);
                        schedule[oldPhaseName] = new PhaseScheduleEntry
                        {
                            StartDate = old?.StartDate,
                            EndDate   = old?.EndDate ?? today,
                            Progress  = (old?.Progress ?? 0) < 100 ? 100.0 : old!.Progress
                        };
                    }
                }

                // Open the new phase (fill start date if not already set).
                var newPhaseName = newStatus.ToString();
                if (phases.Contains(newPhaseName))
                {
                    schedule.TryGetValue(newPhaseName, out var newEntry);
                    if (newEntry?.StartDate == null)
                    {
                        schedule[newPhaseName] = new PhaseScheduleEntry
                        {
                            StartDate = today,
                            EndDate   = newEntry?.EndDate,
                            Progress  = newEntry?.Progress
                        };
                    }
                }
            }

            // Derive legacy fields from the new active phase for backward compat.
            schedule.TryGetValue(newStatus.ToString(), out var activeEntry);
            updated = new PhaseData
            {
                Status         = newStatus,
                PhaseStartDate = activeEntry?.StartDate ?? current?.PhaseStartDate,
                PhaseEndDate   = activeEntry?.EndDate   ?? current?.PhaseEndDate,
                Schedule       = schedule.Count > 0 ? schedule : null
            };
            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
        {
            chapterVm.ApplyPhaseData(updated);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = updated.Status;
                PhaseStartDate = updated.PhaseStartDate;
                PhaseEndDate = updated.PhaseEndDate;
            });
        }
    }

    private async Task SaveDatesAsync()
    {
        var chapterVm = _activeChapterVm;
        var uuid = _loadedUuid;
        var workspaceRoot = _workspaceViewModel.WorkspaceRoot;

        if (chapterVm == null || string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(workspaceRoot))
            return;

        // Capture current field values (may have been edited by user directly).
        var startDate = PhaseStartDate;
        var endDate = PhaseEndDate;
        var currentStatus = Status;

        PhaseData? updated = null;
        await _workspaceViewModel.MetadataStore.UpsertPhaseDataAsync(workspaceRoot, chapterVm.Story, current =>
        {
            updated = new PhaseData
            {
                Status = current?.Status ?? currentStatus,
                PhaseStartDate = startDate,
                PhaseEndDate = endDate
            };
            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
            chapterVm.ApplyPhaseData(updated);
    }

    private async Task SaveScheduleAsync()
    {
        _scheduleSavePending = false;
        var chapterVm = _activeChapterVm;
        var uuid = _loadedUuid;
        var workspaceRoot = _workspaceViewModel.WorkspaceRoot;

        if (chapterVm == null || string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(workspaceRoot))
            return;

        // Snapshot all rows before async gap.
        var rows = PhaseScheduleRows.ToList();
        var currentStatus = Status;

        PhaseData? updated = null;
        await _workspaceViewModel.MetadataStore.UpsertPhaseDataAsync(workspaceRoot, chapterVm.Story, current =>
        {
            // Keep entries for phases this profile doesn't show (e.g. novel phases in a story).
            var rowNames = rows.Select(r => r.PhaseName).ToHashSet(StringComparer.Ordinal);
            var schedule = current?.Schedule?
                .Where(kv => !rowNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new Dictionary<string, PhaseScheduleEntry>();
            foreach (var row in rows)
            {
                // Only persist rows that have at least some data entered.
                if (!string.IsNullOrWhiteSpace(row.StartDate) ||
                    !string.IsNullOrWhiteSpace(row.EndDate) ||
                    row.Progress.HasValue)
                {
                    schedule[row.PhaseName] = new PhaseScheduleEntry
                    {
                        StartDate = string.IsNullOrWhiteSpace(row.StartDate) ? null : row.StartDate.Trim(),
                        EndDate   = string.IsNullOrWhiteSpace(row.EndDate)   ? null : row.EndDate.Trim(),
                        Progress  = row.Progress
                    };
                }
            }

            // Derive legacy fields from the current status phase for backward compat.
            schedule.TryGetValue(currentStatus.ToString(), out var activeEntry);
            updated = new PhaseData
            {
                Status         = current?.Status ?? currentStatus,
                PhaseStartDate = activeEntry?.StartDate ?? current?.PhaseStartDate,
                PhaseEndDate   = activeEntry?.EndDate   ?? current?.PhaseEndDate,
                Schedule       = schedule.Count > 0 ? schedule : null
            };
            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
            chapterVm.ApplyPhaseData(updated);
    }

    private async Task SetTargetAsync(int? minWords)
    {
        var chapterVm = _activeChapterVm;
        if (chapterVm == null) return;

        var preset = SelectedPreset;
        WordCountTarget? target;
        if (!minWords.HasValue)
            target = null;
        else if (preset is { MinWords: not null } && preset.MinWords == minWords)
            target = new WordCountTarget { MinWords = preset.MinWords, MaxWords = preset.MaxWords, Preset = preset.Key };
        else
            target = new WordCountTarget
            {
                MinWords = minWords.Value,
                // Editing only the min keeps the existing max in presets-capable projects.
                MaxWords = HasTargetPresets ? preset?.MaxWords ?? chapterVm.Target?.MaxWords : null,
                Preset = preset is null ? null : TargetPresets.FirstOrDefault(p => p.MinWords is null && p.MaxWords is null)?.Key
            };

        // Delegate persistence to ChapterViewModel.SetTargetCommand — it owns the lock.
        await chapterVm.SetTargetCommand.Execute(target);
    }

    // ── Preference helpers ────────────────────────────────────────────────────

    private async Task LoadPrefillPreferenceAsync()
    {
        try
        {
            var stored = await _settingsStore.GetAsync<bool?>("prefillPhaseDate").ConfigureAwait(false);
            if (stored.HasValue)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _prefillPhaseDate = stored.Value);
        }
        catch
        {
            // Default (true) remains; non-fatal preference load failure is ignored.
        }
    }

    private async Task PersistPrefillPreferenceAsync(bool value)
    {
        try
        {
            await _settingsStore.SetAsync("prefillPhaseDate", value).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; preference may not persist across sessions if store is unavailable.
        }
    }

    /// <summary>
    /// Sets <see cref="IsVisible"/> without requiring an active chapter.
    /// Used by the shell reset-layout command and tests.
    /// </summary>
    public void ApplyVisibility(bool visible)
    {
        _isVisible = visible;
        this.RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>Persists the current <see cref="IsVisible"/> value to the settings store.</summary>
    public Task PersistVisibilityAsync() => PersistChapterInfoVisibilityAsync(_isVisible);

    private async Task PersistChapterInfoVisibilityAsync(bool value)
    {
        try
        {
            await _settingsStore.SetAsync("chapterInfoVisible", value).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; preference may not persist across sessions if storage is unavailable.
        }
    }

    // ── Static helpers ───────────────────────────────────────────────────────

    private static string FormatTargetDisplay(WordCountTarget? target, ProjectProfile profile) =>
        TargetFormatter.Format(target, profile, "—");

    public async Task RestoreSettingsAsync()
    {
        try
        {
            _isVisible = await _settingsStore.GetAsync<bool?>("chapterInfoVisible").ConfigureAwait(false) ?? false;
            this.RaisePropertyChanged(nameof(IsVisible));

            _prefillPhaseDate = await _settingsStore.GetAsync<bool?>("prefillPhaseDate").ConfigureAwait(false) ?? true;
            this.RaisePropertyChanged(nameof(PrefillPhaseDate));
        }
        catch
        {
            _isVisible = false;
            this.RaisePropertyChanged(nameof(IsVisible));
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _chapterDisposables.Dispose();
        _scheduleSaveSubject.Dispose();
        Disposables.Dispose();
        _saveCts.Cancel();
        _saveCts.Dispose();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
