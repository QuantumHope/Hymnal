using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Wraps an immutable <see cref="ChapterNode"/> with mutable reactive status state,
/// word count state, target management, and a <see cref="ChangeStatusCommand"/> that
/// persists phase data to <see cref="PhaseDataService"/>.
/// Lives in the Avalonia project so it may reference ReactiveUI and
/// <see cref="INotificationService"/> without creating circular deps.
/// </summary>
public sealed class ChapterViewModel : ViewModelBase, IDisposable
{
    // ── Injected services ─────────────────────────────────────────────────────

    private readonly PhaseDataService _phaseDataService;
    private readonly TargetsService _targetsService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly INotificationService _notificationService;
    private readonly string _workspaceRoot;

    /// <summary>Raised when the sidebar should show the shared target flyout for this chapter.</summary>
    internal event Action<ChapterViewModel>? TargetFlyoutOpenRequested;

    /// <summary>Raised when the shared target flyout should close for this chapter.</summary>
    internal event Action<ChapterViewModel>? TargetFlyoutCloseRequested;

    // ── Public read-only data ─────────────────────────────────────────────────

    /// <summary>The underlying immutable chapter node (for EditorViewModel etc.).</summary>
    private ChapterNode _node;
    public ChapterNode Node
    {
        get => _node;
        private set => this.RaiseAndSetIfChanged(ref _node, value);
    }

    /// <summary>
    /// Replaces the underlying node (e.g. after a title rename on save)
    /// and raises <see cref="Node"/> property-changed so bound views update.
    /// </summary>
    public void UpdateNode(ChapterNode updated) => Node = updated;

    /// <summary>The stable UUID that survives file renames.</summary>
    public string Uuid { get; }

    // ── Status ────────────────────────────────────────────────────────────────

    private ChapterStatus _status;
    public ChapterStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(StatusLabel));
        }
    }

    /// <summary>Project profile governing statuses, phases and labels for this chapter.</summary>
    public ProjectProfile Profile { get; }

    /// <summary>Profile-specific display label for <see cref="Status"/>.</summary>
    public string StatusLabel => Profile.LabelFor(Status);

    private PhaseData? _phaseData;
    public PhaseData? PhaseData
    {
        get => _phaseData;
        private set => this.RaiseAndSetIfChanged(ref _phaseData, value);
    }

    // ── Word count state ──────────────────────────────────────────────────────

    private int _wordCount;
    /// <summary>Most-recently counted word count for this chapter.</summary>
    public int WordCount
    {
        get => _wordCount;
        private set => this.RaiseAndSetIfChanged(ref _wordCount, value);
    }

    private bool _wordCountKnown;
    /// <summary>False until the first count arrives — sidebar shows '—' when false.</summary>
    public bool WordCountKnown
    {
        get => _wordCountKnown;
        private set => this.RaiseAndSetIfChanged(ref _wordCountKnown, value);
    }

    private int _partTotalWordCount;
    /// <summary>
    /// Only meaningful for Part nodes; populated by WorkspaceViewModel
    /// by summing child chapter word counts.
    /// </summary>
    public int PartTotalWordCount
    {
        get => _partTotalWordCount;
        set => this.RaiseAndSetIfChanged(ref _partTotalWordCount, value);
    }

    private IReadOnlyList<StatusCount> _partStatusSummary = Array.Empty<StatusCount>();
    /// <summary>Only meaningful for Part nodes; status breakdown of child chapters set by WorkspaceViewModel.</summary>
    public IReadOnlyList<StatusCount> PartStatusSummary
    {
        get => _partStatusSummary;
        set => this.RaiseAndSetIfChanged(ref _partStatusSummary, value);
    }

    private bool _isExpanded = true;
    /// <summary>Only meaningful for Part nodes; controls whether child chapters are visible in the sidebar.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }


    // ── Target state ──────────────────────────────────────────────────────────

    private WordCountTarget? _target;
    public WordCountTarget? Target
    {
        get => _target;
        private set => this.RaiseAndSetIfChanged(ref _target, value);
    }

    // ── Target staging (for flyout pending edits) ─────────────────────────────

    private int? _pendingMinWords;
    public int? PendingMinWords
    {
        get => _pendingMinWords;
        set => this.RaiseAndSetIfChanged(ref _pendingMinWords, value);
    }

    private int? _pendingMaxWords;
    public int? PendingMaxWords
    {
        get => _pendingMaxWords;
        set => this.RaiseAndSetIfChanged(ref _pendingMaxWords, value);
    }

    // ── Computed display properties (OAPH) ────────────────────────────────────

    private readonly ObservableAsPropertyHelper<string> _wordCountDisplay;
    /// <summary>'—' until WordCountKnown; then e.g. '2,130 w'.</summary>
    public string WordCountDisplay => _wordCountDisplay.Value;

    private string _wordCountTooltip = "Word count loading…";
    /// <summary>Full word count tooltip, including target range when set.</summary>
    public string WordCountTooltip
    {
        get => _wordCountTooltip;
        private set => this.RaiseAndSetIfChanged(ref _wordCountTooltip, value);
    }

    private string _partTotalDisplay = "0 w";
    /// <summary>Part-total word count in the same display format.</summary>
    public string PartTotalDisplay
    {
        get => _partTotalDisplay;
        private set => this.RaiseAndSetIfChanged(ref _partTotalDisplay, value);
    }

    private string _partTotalTooltip = "0 words in this section";
    /// <summary>Tooltip for the part-total word count.</summary>
    public string PartTotalTooltip
    {
        get => _partTotalTooltip;
        private set => this.RaiseAndSetIfChanged(ref _partTotalTooltip, value);
    }

    private readonly ObservableAsPropertyHelper<double> _proximityFill;
    /// <summary>
    /// 0.0–1.0 fill fraction for the proximity bar.
    /// 0.0 when no Target, else min(WordCount / effectiveMax, 1.0).
    /// </summary>
    public double ProximityFill => _proximityFill.Value;

    private readonly ObservableAsPropertyHelper<bool> _hasTarget;
    public bool HasTarget => _hasTarget.Value;

    private readonly ObservableAsPropertyHelper<bool> _canCompletePhase;
    public bool CanCompletePhase => _canCompletePhase.Value;

    private string _currentPhaseStartDisplay = "—";
    public string CurrentPhaseStartDisplay
    {
        get => _currentPhaseStartDisplay;
        private set => this.RaiseAndSetIfChanged(ref _currentPhaseStartDisplay, value);
    }

    private string _currentPhaseEndDisplay = "—";
    public string CurrentPhaseEndDisplay
    {
        get => _currentPhaseEndDisplay;
        private set => this.RaiseAndSetIfChanged(ref _currentPhaseEndDisplay, value);
    }

    private string _currentPhaseProgressDisplay = "—";
    public string CurrentPhaseProgressDisplay
    {
        get => _currentPhaseProgressDisplay;
        private set => this.RaiseAndSetIfChanged(ref _currentPhaseProgressDisplay, value);
    }

    // ── Flyout open/close state ───────────────────────────────────────────────

    private bool _isTargetFlyoutOpen;
    /// <summary>True while the Set Target popup is open in the sidebar.</summary>
    public bool IsTargetFlyoutOpen
    {
        get => _isTargetFlyoutOpen;
        set => this.RaiseAndSetIfChanged(ref _isTargetFlyoutOpen, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<ChapterStatus, Unit> ChangeStatusCommand { get; }

    /// <summary>Persist (or remove when null) a target for this chapter.</summary>
    public ReactiveCommand<WordCountTarget?, Unit> SetTargetCommand { get; }

    /// <summary>Constructs a target from PendingMin/MaxWords and executes SetTargetCommand.</summary>
    public ReactiveCommand<Unit, Unit> ConfirmTargetCommand { get; }

    /// <summary>Clears the target for this chapter.</summary>
    public ReactiveCommand<Unit, Unit> ClearTargetCommand { get; }

    /// <summary>Opens the Set Target popup (sets IsTargetFlyoutOpen = true).</summary>
    public ReactiveCommand<Unit, Unit> OpenTargetFlyoutCommand { get; }

    /// <summary>Cancels without saving (sets IsTargetFlyoutOpen = false).</summary>
    public ReactiveCommand<Unit, Unit> CancelTargetFlyoutCommand { get; }

    /// <summary>
    /// Marks the current phase 100% complete with dates and advances to the next status.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CompletePhaseCommand { get; }

    /// <summary>Toggles IsExpanded on Part nodes to show/hide child chapters.</summary>
    public ReactiveCommand<Unit, Unit> ToggleExpandedCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public ChapterViewModel(
        ChapterNode node,
        string uuid,
        PhaseData? phaseData,
        PhaseDataService phaseDataService,
        TargetsService targetsService,
        IAppSettingsStore settingsStore,
        INotificationService notificationService,
        string workspaceRoot,
        WordCountTarget? target = null,
        ProjectProfile? profile = null)
    {
        _node = node;        Uuid = uuid;
        Profile = profile ?? ProjectProfile.Novel;
        _phaseData = phaseData;
        _status = phaseData?.Status ?? Profile.DefaultStatus;

        _phaseDataService = phaseDataService;
        _targetsService = targetsService;
        _settingsStore = settingsStore;
        _notificationService = notificationService;
        _workspaceRoot = workspaceRoot;

        _target = target;
        _pendingMinWords = target?.MinWords;
        _pendingMaxWords = target?.MaxWords;

        // ── OAPH: WordCountDisplay ────────────────────────────────────────────
        _wordCountDisplay = this
            .WhenAnyValue(x => x.WordCountKnown, x => x.WordCount,
                (known, count) => known ? $"{count:N0} w" : "—")
            .ToProperty(this, x => x.WordCountDisplay, out _wordCountDisplay);
        Disposables.Add(_wordCountDisplay);

        Disposables.Add(
            this.WhenAnyValue(x => x.WordCountKnown, x => x.WordCount, x => x.Target)
                .Subscribe(tuple => WordCountTooltip = FormatWordCountTooltip(tuple.Item1, tuple.Item2, tuple.Item3)));

        Disposables.Add(
            this.WhenAnyValue(x => x.PartTotalWordCount)
                .Subscribe(total =>
                {
                    PartTotalDisplay = $"{total:N0} w";
                    PartTotalTooltip = $"{total:N0} words in this section";
                }));

        // ── OAPH: ProximityFill ───────────────────────────────────────────────
        _proximityFill = this
            .WhenAnyValue(x => x.Target, x => x.WordCount,
                (t, count) =>
                {
                    if (t is null) return 0.0;
                    var effectiveMax = t.MaxWords ?? t.MinWords ?? 1;
                    return Math.Min((double)count / effectiveMax, 1.0);
                })
            .ToProperty(this, x => x.ProximityFill, out _proximityFill);
        Disposables.Add(_proximityFill);

        // ── OAPH: HasTarget ───────────────────────────────────────────────────
        _hasTarget = this
            .WhenAnyValue(x => x.Target,
                t => t != null && (t.MinWords.HasValue || t.MaxWords.HasValue))
            .ToProperty(this, x => x.HasTarget, out _hasTarget);
        Disposables.Add(_hasTarget);

        // ── OAPH: CanCompletePhase ────────────────────────────────────────────
        _canCompletePhase = this
            .WhenAnyValue(x => x.Status, x => x.Node.Kind,
                (status, kind) => kind == NodeKind.Chapter && status != ChapterStatus.Done)
            .ToProperty(this, x => x.CanCompletePhase, out _canCompletePhase);
        Disposables.Add(_canCompletePhase);

        Disposables.Add(
            this.WhenAnyValue(x => x.Status, x => x.PhaseData)
                .Subscribe(tuple =>
                {
                    CurrentPhaseStartDisplay = FormatCurrentPhaseDate(tuple.Item1, tuple.Item2, seg => seg?.StartDate);
                    CurrentPhaseEndDisplay = FormatCurrentPhaseDate(tuple.Item1, tuple.Item2, seg => seg?.EndDate);
                    CurrentPhaseProgressDisplay = FormatCurrentPhaseProgress(tuple.Item1, tuple.Item2);
                }));

        // ── Commands ──────────────────────────────────────────────────────────

        ChangeStatusCommand = ReactiveCommand.CreateFromTask<ChapterStatus>(ChangeStatusAsync);
        Disposables.Add(
            ChangeStatusCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        SetTargetCommand = ReactiveCommand.CreateFromTask<WordCountTarget?>(SetTargetAsync);
        Disposables.Add(
            SetTargetCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        ConfirmTargetCommand = ReactiveCommand.CreateFromTask(ConfirmTargetAsync);
        Disposables.Add(
            ConfirmTargetCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        ClearTargetCommand = ReactiveCommand.CreateFromTask(ClearTargetAsync);
        Disposables.Add(
            ClearTargetCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        OpenTargetFlyoutCommand = ReactiveCommand.Create(() => TargetFlyoutOpenRequested?.Invoke(this));
        Disposables.Add(
            OpenTargetFlyoutCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        CancelTargetFlyoutCommand = ReactiveCommand.Create(() => TargetFlyoutCloseRequested?.Invoke(this));
        Disposables.Add(
            CancelTargetFlyoutCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        CompletePhaseCommand = ReactiveCommand.CreateFromTask(
            CompletePhaseAsync,
            this.WhenAnyValue(x => x.CanCompletePhase));
        Disposables.Add(
            CompletePhaseCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError(ex.Message)));

        ToggleExpandedCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
    }

    // ── Public mutators ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by WorkspaceViewModel after counting words for an opened or saved chapter.
    /// Sets WordCount and marks WordCountKnown = true so the sidebar replaces '—'.
    /// </summary>
    public void UpdateWordCount(int count)
    {
        WordCount = count;
        WordCountKnown = true;
    }

    /// <summary>
    /// Persists new phase start/end dates without changing the chapter's status.
    /// Date strings must be ISO 8601 (yyyy-MM-dd) or null to clear the date.
    /// Called by <see cref="GanttViewModel.CommitEditCommand"/> when the inline
    /// date-picker overlay is committed.
    /// </summary>
    public async Task UpdateDatesAsync(string? startDate, string? endDate)
    {
        PhaseData? updated = null;
        await _phaseDataService.UpsertAsync(_workspaceRoot, Uuid, current =>
        {
            var basePhaseData = current ?? _phaseData;
            updated = new PhaseData
            {
                Status         = basePhaseData?.Status ?? Profile.DefaultStatus,
                PhaseStartDate = startDate,
                PhaseEndDate   = endDate
            };
            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
            ApplyPhaseData(updated);
    }

    /// <summary>
    /// Re-syncs observable status and phase-data state after ChapterInfoViewModel
    /// has persisted a change via PhaseDataService directly.
    /// Safe to call from any thread — posts to UIThread if not already on it.
    /// </summary>
    public void ApplyPhaseData(PhaseData phaseData)
    {
        void Apply()
        {
            Status = phaseData.Status;
            PhaseData = phaseData;
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else if (Application.Current is null)
        {
            // Unit tests and other non-Avalonia hosts have no dispatcher loop.
            Apply();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Apply).GetAwaiter().GetResult();
        }
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task ChangeStatusAsync(ChapterStatus newStatus)
    {
        PhaseData? updated = null;
        await _phaseDataService.UpsertAsync(_workspaceRoot, Uuid, current =>
        {
            var basePhaseData = current ?? _phaseData;
            updated = new PhaseData
            {
                Status = newStatus,
                PhaseStartDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                PhaseEndDate = basePhaseData?.PhaseEndDate
            };
            return updated;
        }).ConfigureAwait(false);

        // Commit state updates on the UI thread.
        await Avalonia.Threading.Dispatcher.UIThread
            .InvokeAsync(() =>
            {
                Status = newStatus;
                PhaseData = updated!;
            });
    }

    private async Task SetTargetAsync(WordCountTarget? target)
    {
        await _targetsService.UpsertAsync(_workspaceRoot, Uuid, target).ConfigureAwait(false);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Target = target;
            PendingMinWords = target?.MinWords;
            PendingMaxWords = target?.MaxWords;
        });
    }

    private async Task ConfirmTargetAsync()
    {
        var newTarget = new WordCountTarget
        {
            MinWords = PendingMinWords,
            MaxWords = PendingMaxWords
        };
        await SetTargetAsync(newTarget).ConfigureAwait(false);
        TargetFlyoutCloseRequested?.Invoke(this);
    }

    private async Task ClearTargetAsync()
    {
        await SetTargetAsync(null).ConfigureAwait(false);
        TargetFlyoutCloseRequested?.Invoke(this);
    }

    /// <summary>Marks the current phase complete and advances status. Callable from other ViewModels.</summary>
    public Task CompleteCurrentPhaseAsync() => CompletePhaseAsync();

    private async Task CompletePhaseAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        PhaseData? updated = null;
        string? error = null;

        await _phaseDataService.UpsertAsync(_workspaceRoot, Uuid, current =>
        {
            var basePhaseData = current ?? _phaseData ?? PhaseDataService.DefaultPhaseDataFor(Profile);
            var result = PhaseCompletion.CompleteAndAdvance(basePhaseData, today, Profile);
            if (!result.IsSuccess)
            {
                error = result.Error;
                return basePhaseData;
            }

            updated = result.Value!;
            return updated!;
        }).ConfigureAwait(false);

        if (error != null)
        {
            _notificationService.ShowError(error);
            return;
        }

        if (updated != null)
            ApplyPhaseData(updated);
    }

    private static string FormatWordCountTooltip(bool known, int count, WordCountTarget? target)
    {
        if (!known) return "Word count loading…";
        var sb = new System.Text.StringBuilder($"{count:N0} words");
        if (target != null)
        {
            if (target.MinWords.HasValue && target.MaxWords.HasValue)
                sb.Append($"  ·  Target: {target.MinWords:N0} – {target.MaxWords:N0}");
            else if (target.MinWords.HasValue)
                sb.Append($"  ·  Min: {target.MinWords:N0}");
            else if (target.MaxWords.HasValue)
                sb.Append($"  ·  Max: {target.MaxWords:N0}");
        }

        return sb.ToString();
    }

    private string FormatCurrentPhaseDate(
        ChapterStatus status,
        PhaseData? phaseData,
        Func<PhaseSegment?, DateOnly?> selector)
    {
        var seg = GetCurrentPhaseSegment(status, phaseData);
        var date = selector(seg);
        return date?.ToString("yyyy-MM-dd") ?? "—";
    }

    private string FormatCurrentPhaseProgress(ChapterStatus status, PhaseData? phaseData)
    {
        var seg = GetCurrentPhaseSegment(status, phaseData);
        if (seg is null || seg.Progress <= 0)
            return "—";

        return $"{Math.Round(seg.Progress * 100.0):0}%";
    }

    private PhaseSegment? GetCurrentPhaseSegment(ChapterStatus status, PhaseData? phaseData)
    {
        if (phaseData is null)
            return null;

        return GanttProjection.BuildSegments(phaseData, Profile)
            .FirstOrDefault(s => string.Equals(s.PhaseName, status.ToString(), StringComparison.Ordinal));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposables.Dispose();
    }
}
