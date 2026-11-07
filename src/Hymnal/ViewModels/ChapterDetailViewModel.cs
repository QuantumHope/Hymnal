using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Backs the <see cref="Hymnal.Views.ChapterDetailDialog"/> modal opened from the Gantt "View details" context menu.
/// Wraps a specific <see cref="ChapterViewModel"/> and exposes editable status/schedule/target
/// properties without requiring an active editor node.
/// </summary>
public sealed class ChapterDetailViewModel : ViewModelBase, IDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly ChapterViewModel _chapterVm;
    private readonly PhaseDataService _phaseDataService;
    private readonly string _workspaceRoot;

    private readonly CompositeDisposable _innerDisposables = new();
    private readonly Subject<Unit> _scheduleSaveSubject = new();
    private bool _suppressAutoSave;

    // ── Profile-driven data ───────────────────────────────────────────────────

    /// <summary>Project profile of the backing chapter.</summary>
    public ProjectProfile Profile => _chapterVm.Profile;

    /// <summary>Statuses visible in this profile; bound to the status ComboBox ItemsSource.</summary>
    public IReadOnlyList<StatusOption> StatusOptions => Profile.VisibleStatuses;

    // ── Bindable properties ───────────────────────────────────────────────────

    public string ChapterTitle { get; }

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

    public ObservableCollection<PhaseScheduleRowViewModel> PhaseScheduleRows { get; }

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
    public int? PendingTarget
    {
        get => _pendingTarget;
        set => this.RaiseAndSetIfChanged(ref _pendingTarget, value);
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
    public bool ScheduleSavedRecently
    {
        get => _scheduleSavedRecently;
        private set => this.RaiseAndSetIfChanged(ref _scheduleSavedRecently, value);
    }

    private bool _prefillPhaseDate = true;
    public bool PrefillPhaseDate
    {
        get => _prefillPhaseDate;
        set => this.RaiseAndSetIfChanged(ref _prefillPhaseDate, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<ChapterStatus, Unit> SetStatusCommand { get; }
    public ReactiveCommand<int?, Unit> SetTargetCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public ChapterDetailViewModel(
        ChapterViewModel chapterVm,
        PhaseDataService phaseDataService,
        string workspaceRoot,
        INotificationService notificationService)
    {
        _chapterVm = chapterVm;
        _phaseDataService = phaseDataService;
        _workspaceRoot = workspaceRoot;

        ChapterTitle = chapterVm.Node.Title;
        _status = chapterVm.Status;
        _wordCount = chapterVm.WordCount;
        _hasTarget = chapterVm.HasTarget;
        _proximityFill = chapterVm.ProximityFill;
        _targetDisplay = FormatTargetDisplay(chapterVm.Target, chapterVm.Profile);
        _pendingTarget = chapterVm.Target?.MinWords;

        PhaseScheduleRows = new ObservableCollection<PhaseScheduleRowViewModel>(
            chapterVm.Profile.GanttPhases.Select(n => new PhaseScheduleRowViewModel(n)));

        SyncScheduleRows(chapterVm.PhaseData);

        // ── Live subscriptions from the backing ChapterViewModel ───────────────
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.Status)
            .Subscribe(s => Status = s));
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.WordCount)
            .Subscribe(c => WordCount = c));
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.HasTarget)
            .Subscribe(h => HasTarget = h));
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.ProximityFill)
            .Subscribe(f => ProximityFill = f));
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.Target)
            .Subscribe(t => TargetDisplay = FormatTargetDisplay(t, Profile)));
        _innerDisposables.Add(chapterVm.WhenAnyValue(x => x.PhaseData)
            .Subscribe(pd => SyncScheduleRows(pd)));

        // ── Word count display OAPH ───────────────────────────────────────────
        _wordCountDisplay = this
            .WhenAnyValue(x => x.WordCount)
            .Select(c => $"{c:N0} w")
            .ToProperty(this, x => x.WordCountDisplay, out _wordCountDisplay);
        Disposables.Add(_wordCountDisplay);

        // ── Schedule auto-save (debounced, mirrors ChapterInfoViewModel) ──────
        foreach (var row in PhaseScheduleRows)
        {
            _innerDisposables.Add(
                row.WhenAnyValue(x => x.StartDate, x => x.EndDate, x => x.Progress)
                    .Skip(1)
                    .Subscribe(_ =>
                    {
                        if (!_suppressAutoSave)
                            _scheduleSaveSubject.OnNext(Unit.Default);
                    }));
        }

        _innerDisposables.Add(
            _scheduleSaveSubject
                .Throttle(TimeSpan.FromMilliseconds(800), TaskPoolScheduler.Default)
                .Subscribe(async _ => await AutoSaveScheduleAsync().ConfigureAwait(false)));

        // ── Commands ──────────────────────────────────────────────────────────
        SetStatusCommand = ReactiveCommand.CreateFromTask<ChapterStatus>(SetStatusAsync);
        Disposables.Add(SetStatusCommand.ThrownExceptions
            .Subscribe(ex => notificationService.ShowError($"Set status failed: {ex.Message}")));

        SetTargetCommand = ReactiveCommand.CreateFromTask<int?>(SetTargetAsync);
        Disposables.Add(SetTargetCommand.ThrownExceptions
            .Subscribe(ex => notificationService.ShowError($"Set target failed: {ex.Message}")));
    }

    // ── Schedule helpers ──────────────────────────────────────────────────────

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

    /// <summary>Triggers an immediate (non-debounced) schedule save; called from LostFocus handler.</summary>
    public void RequestImmediateScheduleSave()
    {
        if (!_suppressAutoSave)
            _ = AutoSaveScheduleAsync();
    }

    private async Task AutoSaveScheduleAsync()
    {
        await SaveScheduleAsync().ConfigureAwait(false);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ScheduleSavedRecently = true);
        await Task.Delay(2000).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ScheduleSavedRecently = false);
    }

    private async Task SaveScheduleAsync()
    {
        var rows = PhaseScheduleRows.ToList();
        var currentStatus = Status;

        PhaseData? updated = null;
        await _phaseDataService.UpsertAsync(_workspaceRoot, _chapterVm.Uuid, current =>
        {
            // Keep entries for phases this profile doesn't show (e.g. novel phases in a story).
            var rowNames = rows.Select(r => r.PhaseName).ToHashSet(StringComparer.Ordinal);
            var schedule = current?.Schedule?
                .Where(kv => !rowNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new Dictionary<string, PhaseScheduleEntry>();
            foreach (var row in rows)
            {
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
            _chapterVm.ApplyPhaseData(updated);
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task SetStatusAsync(ChapterStatus newStatus)
    {
        PhaseData? updated = null;
        await _phaseDataService.UpsertAsync(_workspaceRoot, _chapterVm.Uuid, current =>
        {
            var statusChanged = current?.Status != newStatus;
            var isAdvancing = (int)newStatus > (int)(current?.Status ?? ChapterStatus.Planned);

            var schedule = current?.Schedule != null
                ? new Dictionary<string, PhaseScheduleEntry>(current.Schedule)
                : new Dictionary<string, PhaseScheduleEntry>();

            if (_prefillPhaseDate && statusChanged)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                if (isAdvancing)
                {
                    // Close the segment the old status displays as (story Polishing → Editing).
                    var oldPhaseName = current is null ? null : Profile.DisplayStatus(current.Status).ToString();
                    if (oldPhaseName != null && Profile.GanttPhases.Contains(oldPhaseName))
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

                var newPhaseName = newStatus.ToString();
                if (Profile.GanttPhases.Contains(newPhaseName))
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
            _chapterVm.ApplyPhaseData(updated);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = updated.Status;
            });
        }
    }

    private async Task SetTargetAsync(int? minWords)
    {
        WordCountTarget? target = minWords.HasValue
            ? new WordCountTarget { MinWords = minWords.Value }
            : null;

        await _chapterVm.SetTargetCommand.Execute(target);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static string FormatTargetDisplay(WordCountTarget? target, ProjectProfile profile) =>
        TargetFormatter.Format(target, profile, "—");

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _innerDisposables.Dispose();
        _scheduleSaveSubject.Dispose();
        Disposables.Dispose();
    }
}
