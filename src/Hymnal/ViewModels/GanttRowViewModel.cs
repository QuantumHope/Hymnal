using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Hymnal.Core.Models;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Represents a single row in the read-only Gantt chart.
/// Wraps an immutable <see cref="GanttRowData"/> produced by
/// <see cref="Hymnal.Core.Services.GanttProjection.Project"/>.
/// Part rows are included so the view can render section dividers inline.
/// </summary>
public sealed class GanttRowViewModel : ViewModelBase
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Forward-slash-normalized path relative to manuscript root.</summary>
    public string RelativePath { get; }

    /// <summary>Chapter or Part title shown in the row label column.</summary>
    public string Title { get; }

    /// <summary>Whether this row represents a Part (section divider) or a Chapter.</summary>
    public NodeKind Kind { get; }

    // ── Status / dates ────────────────────────────────────────────────────────

    /// <summary>Current authoring phase for this chapter.</summary>
    public ChapterStatus Status { get; }

    /// <summary>Parsed phase start date, or null when the value is absent/unparseable.</summary>
    public DateOnly? StartDate { get; }

    /// <summary>Parsed phase end date, or null when the value is absent/unparseable.</summary>
    public DateOnly? EndDate { get; }

    /// <summary>True when either StartDate or EndDate is null — row renders in a muted style.</summary>
    public bool IsMissingDates { get; }

    /// <summary>
    /// Fractional completion in the range [0.0, 1.0].
    /// For Part rollup rows this reflects child chapter completion (fraction with status Done).
    /// For Chapter rows this is always 0.0 (chapter-level progress is not tracked here).
    /// </summary>
    public double CompletionPercentage { get; }

    /// <summary>
    /// Per-phase timeline segments for rendering multiple colored bars on the Gantt canvas.
    /// Empty for Part rows.
    /// </summary>
    public IReadOnlyList<PhaseSegment> PhaseSegments { get; }

    // ── Convenience ───────────────────────────────────────────────────────────

    public bool IsChapter => Kind == NodeKind.Chapter;
    public bool IsPart    => Kind == NodeKind.Part && !IsBook;
    public bool IsBook    { get; }
    public bool IsEditable => IsChapter && !IsBook;
    public GanttEditableColumn PendingEditColumn { get; set; } = GanttEditableColumn.None;

    /// <summary>Project profile this row was built for.</summary>
    public ProjectProfile Profile { get; }

    /// <summary>Statuses visible in <see cref="Profile"/>; bound to the inline status ComboBox.</summary>
    public IReadOnlyList<StatusOption> AvailableStatuses => Profile.VisibleStatuses;

    private ChapterStatus _editableStatus;
    /// <summary>Stored status being edited (may be one the profile only displays via mapping).</summary>
    public ChapterStatus EditableStatus
    {
        get => _editableStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _editableStatus, value);
            this.RaisePropertyChanged(nameof(EditableDisplayStatus));
            this.RaisePropertyChanged(nameof(StatusDisplay));
            this.RaisePropertyChanged(nameof(StatusForeground));
        }
    }

    /// <summary>
    /// <see cref="EditableStatus"/> as shown by <see cref="Profile"/>. Setting a value in the same
    /// display bucket keeps the stored status unchanged.
    /// </summary>
    public ChapterStatus EditableDisplayStatus
    {
        get => Profile.DisplayStatus(EditableStatus);
        set
        {
            if (value != Profile.DisplayStatus(EditableStatus))
                EditableStatus = value;
        }
    }

    private DateTime? _editableStartDate;
    public DateTime? EditableStartDate
    {
        get => _editableStartDate;
        set
        {
            this.RaiseAndSetIfChanged(ref _editableStartDate, value);
            this.RaisePropertyChanged(nameof(StartDateDisplay));
        }
    }

    private DateTime? _editableEndDate;
    public DateTime? EditableEndDate
    {
        get => _editableEndDate;
        set
        {
            this.RaiseAndSetIfChanged(ref _editableEndDate, value);
            this.RaisePropertyChanged(nameof(EndDateDisplay));
        }
    }

    private double? _editableProgressPercent;
    public double? EditableProgressPercent
    {
        get => _editableProgressPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _editableProgressPercent, value);
            this.RaisePropertyChanged(nameof(ProgressDisplay));
        }
    }

    public string RowBackground => IsBook
        ? "#2A1D46"
        : IsPart
            ? "#1A1235"
            : "Transparent";

    public string RowForeground => IsBook
        ? "#E9D5FF"
        : IsPart
            ? "#94A3B8"
            : "#CBD5E1";

    public string StatusForeground => IsEditable
        ? StatusToColorHex(EditableStatus)
        : RowForeground;

    public string StatusDisplay => IsEditable ? Profile.LabelFor(EditableStatus) : "Rollup";

    /// <summary><see cref="Status"/> mapped onto a status visible in <see cref="Profile"/>.</summary>
    public ChapterStatus DisplayStatus => Profile.DisplayStatus(Status);

    /// <summary>Profile label for the stored <see cref="Status"/> (Novel: the enum name).</summary>
    public string StatusLabel => Profile.LabelFor(Status);

    /// <summary>Phase segment for the row's current (display) status, if any.</summary>
    public PhaseSegment? CurrentSegment => GetCurrentStatusSegment(Status);

    /// <summary>True when <paramref name="phaseName"/> is the phase the row's display status sits in.</summary>
    public bool IsActivePhase(string phaseName) =>
        string.Equals(phaseName, DisplayStatus.ToString(), StringComparison.Ordinal);

    public string StartDateDisplay
    {
        get
        {
            return EditableStartDate?.ToString("yyyy-MM-dd") ?? "--";
        }
    }

    public string EndDateDisplay
    {
        get
        {
            return EditableEndDate?.ToString("yyyy-MM-dd") ?? "--";
        }
    }

    public string ProgressDisplay
    {
        get
        {
            var percent = EditableProgressPercent ?? 0;
            if (percent <= 0)
                return "--";

            return $"{Math.Round(percent):0}%";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired by <see cref="Hymnal.Views.GanttCanvas"/> when the user clicks this
    /// chapter row to edit its phase dates. Part rows also carry this command but
    /// the canvas only invokes it on chapter rows. Observers (typically
    /// <see cref="GanttViewModel"/>) subscribe and open the date-picker popup.
    /// </summary>
    public ReactiveCommand<Unit, Unit> EditDatesCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public GanttRowViewModel(GanttRowData data, ProjectProfile? profile = null)
    {
        Profile              = profile ?? ProjectProfile.Novel;
        RelativePath         = data.RelativePath;
        Title                = data.Title;
        Kind                 = data.Kind;
        Status               = data.Status;
        StartDate            = data.StartDate;
        EndDate              = data.EndDate;
        IsMissingDates       = data.IsMissingDates;
        CompletionPercentage = data.CompletionPercentage;
        PhaseSegments        = data.PhaseSegments ?? Array.Empty<PhaseSegment>();
        IsBook               = data.IsBook;

        var currentSeg = GetCurrentStatusSegment(Status);
        var initialStart = currentSeg?.StartDate ?? StartDate;
        var initialEnd = currentSeg?.EndDate ?? EndDate;

        _editableStatus = Status;
        _editableStartDate = initialStart?.ToDateTime(TimeOnly.MinValue);
        _editableEndDate = initialEnd?.ToDateTime(TimeOnly.MinValue);
        _editableProgressPercent = currentSeg is null ? 0.0 : Math.Round(currentSeg.Progress * 100.0, 0);

        EditDatesCommand = ReactiveCommand.Create(() => { });
    }

    private PhaseSegment? GetCurrentStatusSegment(ChapterStatus status)
    {
        return PhaseSegments.FirstOrDefault(s =>
            string.Equals(s.PhaseName, Profile.DisplayStatus(status).ToString(), StringComparison.Ordinal));
    }

    internal static string StatusToColorHex(ChapterStatus status)
    {
        return status switch
        {
            ChapterStatus.Planned => "#FF6B35",
            ChapterStatus.Outlining => "#9589B0",
            ChapterStatus.Drafting => "#38BDF8",
            ChapterStatus.Editing => "#9D4EDD",
            ChapterStatus.Polishing => "#F5C842",
            ChapterStatus.Reviewing => "#E91E8C",
            ChapterStatus.Done => "#22D3A0",
            _ => "#CBD5E1"
        };
    }
}
