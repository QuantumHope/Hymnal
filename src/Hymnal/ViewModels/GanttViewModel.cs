using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Projection of the current workspace chapter list into Gantt rows with cell-driven editing.
/// </summary>
public sealed class GanttViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _workspace;
    private readonly INotificationService _notificationService;

    private readonly ObservableCollection<GanttRowViewModel> _rows = new();
    private readonly SerialDisposable _phaseSubscriptions = new();
    private readonly Subject<GanttRowViewModel> _rowEditRequested = new();

    public ReadOnlyObservableCollection<GanttRowViewModel> Rows { get; }
    public IObservable<GanttRowViewModel> RowEditRequested => _rowEditRequested;

    private GanttRowViewModel? _editingRow;
    public GanttRowViewModel? EditingRow
    {
        get => _editingRow;
        private set => this.RaiseAndSetIfChanged(ref _editingRow, value);
    }

    private bool _isEditingDates;
    public bool IsEditingDates
    {
        get => _isEditingDates;
        private set => this.RaiseAndSetIfChanged(ref _isEditingDates, value);
    }

    private GanttEditableColumn _editingColumn = GanttEditableColumn.None;
    public GanttEditableColumn EditingColumn
    {
        get => _editingColumn;
        private set
        {
            this.RaiseAndSetIfChanged(ref _editingColumn, value);
            this.RaisePropertyChanged(nameof(IsStatusEditorOpen));
            this.RaisePropertyChanged(nameof(IsStartEditorOpen));
            this.RaisePropertyChanged(nameof(IsEndEditorOpen));
            this.RaisePropertyChanged(nameof(IsProgressEditorOpen));
        }
    }

    public bool IsStatusEditorOpen => EditingColumn == GanttEditableColumn.Status;
    public bool IsStartEditorOpen => EditingColumn == GanttEditableColumn.StartDate;
    public bool IsEndEditorOpen => EditingColumn == GanttEditableColumn.EndDate;
    public bool IsProgressEditorOpen => EditingColumn == GanttEditableColumn.Progress;

    private ChapterStatus _editingStatus = ChapterStatus.Outlining;
    public ChapterStatus EditingStatus
    {
        get => _editingStatus;
        private set => this.RaiseAndSetIfChanged(ref _editingStatus, value);
    }

    /// <summary>Statuses visible in the workspace profile.</summary>
    public IReadOnlyList<StatusOption> AvailableStatuses => _workspace.Profile.VisibleStatuses;

    /// <summary>Legend entries: one per Gantt phase of the workspace profile.</summary>
    public IReadOnlyList<GanttLegendItem> LegendItems => _workspace.Profile.GanttPhases
        .Select(name => Enum.Parse<ChapterStatus>(name))
        .Select(s => new GanttLegendItem(_workspace.Profile.LabelFor(s), s))
        .ToList();

    private string _editingPhaseName = string.Empty;
    public string EditingPhaseName
    {
        get => _editingPhaseName;
        private set => this.RaiseAndSetIfChanged(ref _editingPhaseName, value);
    }

    private DateTime? _editStartDate;
    public DateTime? EditStartDate
    {
        get => _editStartDate;
        set => this.RaiseAndSetIfChanged(ref _editStartDate, value);
    }

    private DateTime? _editEndDate;
    public DateTime? EditEndDate
    {
        get => _editEndDate;
        set => this.RaiseAndSetIfChanged(ref _editEndDate, value);
    }

    private double? _editProgressPercent;
    public double? EditProgressPercent
    {
        get => _editProgressPercent;
        set => this.RaiseAndSetIfChanged(ref _editProgressPercent, value);
    }

    private Thickness _editPopupMargin = new(0);
    public Thickness EditPopupMargin
    {
        get => _editPopupMargin;
        private set => this.RaiseAndSetIfChanged(ref _editPopupMargin, value);
    }

    private double _editCellWidth;
    public double EditCellWidth
    {
        get => _editCellWidth;
        private set => this.RaiseAndSetIfChanged(ref _editCellWidth, value);
    }

    private double _editCellHeight;
    public double EditCellHeight
    {
        get => _editCellHeight;
        private set => this.RaiseAndSetIfChanged(ref _editCellHeight, value);
    }

    public ReactiveCommand<Unit, Unit> CommitEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }
    public ReactiveCommand<ChapterStatus, Unit> SetEditingStatusCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearStartDateCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearEndDateCommand { get; }
    public ReactiveCommand<GanttRowViewModel, Unit> MarkRowCompleteCommand { get; }
    public ReactiveCommand<Unit, Unit> CompleteSelectedRowCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveSelectedRowUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveSelectedRowDownCommand { get; }

    private GanttRowViewModel? _selectedRow;
    public GanttRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => this.RaiseAndSetIfChanged(ref _selectedRow, value);
    }

    private bool _isViewActive = true;
    private bool _needsRebuild;

    public GanttViewModel(
        WorkspaceViewModel workspace,
        INotificationService notificationService)
    {
        _workspace = workspace;
        _notificationService = notificationService;

        Rows = new ReadOnlyObservableCollection<GanttRowViewModel>(_rows);

        var nodesAsNotify = (INotifyCollectionChanged)workspace.Nodes;
        NotifyCollectionChangedEventHandler handler = (_, _) => RequestRebuild();
        nodesAsNotify.CollectionChanged += handler;

        Disposables.Add(Disposable.Create(() => nodesAsNotify.CollectionChanged -= handler));
        Disposables.Add(_phaseSubscriptions);
        Disposables.Add(Disposable.Create(() => _rowEditRequested.OnCompleted()));
        Disposables.Add(_rowEditRequested.Subscribe(OpenEditForRow));
        Disposables.Add(workspace.WhenAnyValue(x => x.Profile)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(AvailableStatuses));
                this.RaisePropertyChanged(nameof(LegendItems));
            }));

        CommitEditCommand = ReactiveCommand.CreateFromTask(CommitEditAsync);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        SetEditingStatusCommand = ReactiveCommand.Create<ChapterStatus>(SetEditingStatus);
        ClearStartDateCommand = ReactiveCommand.Create(() => { EditStartDate = null; });
        ClearEndDateCommand = ReactiveCommand.Create(() => { EditEndDate = null; });

        Disposables.Add(CommitEditCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));
        Disposables.Add(CancelEditCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));
        Disposables.Add(SetEditingStatusCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));
        Disposables.Add(ClearStartDateCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));
        Disposables.Add(ClearEndDateCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));

        MarkRowCompleteCommand = ReactiveCommand.CreateFromTask<GanttRowViewModel>(
            MarkRowCompleteAsync,
            this.WhenAnyValue(x => x.SelectedRow).Select(_ => true));
        Disposables.Add(MarkRowCompleteCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));

        CompleteSelectedRowCommand = ReactiveCommand.CreateFromTask(
            CompleteSelectedRowAsync,
            this.WhenAnyValue(x => x.SelectedRow).Select(r => r?.IsEditable == true));
        Disposables.Add(CompleteSelectedRowCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));

        MoveSelectedRowUpCommand = ReactiveCommand.CreateFromTask(
            MoveSelectedRowUpAsync,
            this.WhenAnyValue(x => x.SelectedRow).Select(r => r?.IsEditable == true));
        MoveSelectedRowDownCommand = ReactiveCommand.CreateFromTask(
            MoveSelectedRowDownAsync,
            this.WhenAnyValue(x => x.SelectedRow).Select(r => r?.IsEditable == true));
        Disposables.Add(MoveSelectedRowUpCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));
        Disposables.Add(MoveSelectedRowDownCommand.ThrownExceptions.Subscribe(ex => _notificationService.ShowError(ex.Message)));

        RebuildRows();
    }

    /// <summary>Called when Manage mode becomes active or inactive.</summary>
    public void SetViewActive(bool active)
    {
        _isViewActive = active;
        if (active && _needsRebuild)
        {
            _needsRebuild = false;
            RebuildRows();
        }
    }

    private void RequestRebuild()
    {
        if (!_isViewActive)
        {
            _needsRebuild = true;
            return;
        }

        RebuildRows();
    }

    private void OpenEditForRow(GanttRowViewModel row)
    {
        EditingRow = row;
        EditingColumn = row.PendingEditColumn == GanttEditableColumn.None
            ? GanttEditableColumn.Status
            : row.PendingEditColumn;
        EditingStatus = row.Status;
        LoadEditorValuesForStatus(row, EditingStatus);
        IsEditingDates = true;
    }

    public void BeginCellEdit(GanttRowViewModel row, GanttEditableColumn column, Rect cellBounds)
    {
        row.PendingEditColumn = column;
        EditPopupMargin = new Thickness(cellBounds.X, cellBounds.Y, 0, 0);
        EditCellWidth = cellBounds.Width;
        EditCellHeight = cellBounds.Height;
        OpenEditForRow(row);
    }

    /// <summary>
    /// Creates a <see cref="ChapterDetailViewModel"/> for the given Gantt row so the
    /// view can open the Chapter Details dialog. Returns null if the chapter cannot be found.
    /// </summary>
    public ChapterDetailViewModel? CreateDetailViewModel(GanttRowViewModel row)
    {
        if (!row.IsEditable)
            return null;

        var chapterVm = _workspace.Nodes
            .FirstOrDefault(n => n.Node.RelativePath == row.RelativePath);

        if (chapterVm == null)
        {
            _notificationService.ShowError($"Could not find chapter '{row.Title}'.");
            return null;
        }

        return new ChapterDetailViewModel(
            chapterVm,
            () => _workspace.MetadataStore,
            _workspace.WorkspaceRoot,
            _notificationService);
    }

    public async Task MarkRowCompleteAsync(GanttRowViewModel row)
    {
        if (!row.IsEditable)
            return;

        var chapterVm = _workspace.Nodes
            .FirstOrDefault(n => n.Node.RelativePath == row.RelativePath);

        if (chapterVm == null)
        {
            _notificationService.ShowError($"Could not find chapter '{row.Title}' to mark complete.");
            return;
        }

        await chapterVm.CompleteCurrentPhaseAsync().ConfigureAwait(false);
    }

    private async Task CompleteSelectedRowAsync()
    {
        if (SelectedRow?.IsEditable == true)
            await MarkRowCompleteAsync(SelectedRow).ConfigureAwait(false);
    }

    private Task MoveSelectedRowUpAsync()
    {
        var row = SelectedRow;
        if (!IsIncludedEditableChapter(row))
            return Task.CompletedTask;

        var index = _rows.IndexOf(row!);
        if (index <= 0)
            return Task.CompletedTask;

        for (var i = index - 1; i >= 0; i--)
        {
            if (IsIncludedEditableChapter(_rows[i]))
                return MoveRowBeforeAsync(row!, _rows[i]);
        }

        return Task.CompletedTask;
    }

    private Task MoveSelectedRowDownAsync()
    {
        var row = SelectedRow;
        if (!IsIncludedEditableChapter(row))
            return Task.CompletedTask;

        var index = _rows.IndexOf(row!);
        if (index < 0 || index >= _rows.Count - 1)
            return Task.CompletedTask;

        for (var i = index + 1; i < _rows.Count; i++)
        {
            if (IsIncludedEditableChapter(_rows[i]))
                return MoveRowAfterAsync(row!, _rows[i]);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reorders an included chapter row before another included chapter row by delegating
    /// to the workspace's canonical Book.txt reorder command.
    /// </summary>
    public Task MoveRowBeforeAsync(GanttRowViewModel? sourceRow, GanttRowViewModel? targetRow)
    {
        if (!CanReorderRows(sourceRow, targetRow))
            return Task.CompletedTask;

        if (IsImmediatelyBefore(sourceRow!, targetRow!))
            return Task.CompletedTask;

        return ExecuteWorkspaceReorderAsync(new ReorderCardRequest(
            sourceRow!.RelativePath,
            BeforeRelativePath: targetRow!.RelativePath));
    }

    /// <summary>
    /// Reorders an included chapter row after another included chapter row by delegating
    /// to the workspace's canonical Book.txt reorder command.
    /// </summary>
    public Task MoveRowAfterAsync(GanttRowViewModel? sourceRow, GanttRowViewModel? targetRow)
    {
        if (!CanReorderRows(sourceRow, targetRow))
            return Task.CompletedTask;

        if (IsImmediatelyAfter(sourceRow!, targetRow!))
            return Task.CompletedTask;

        return ExecuteWorkspaceReorderAsync(new ReorderCardRequest(
            sourceRow!.RelativePath,
            AfterRelativePath: targetRow!.RelativePath));
    }

    private bool CanReorderRows(GanttRowViewModel? sourceRow, GanttRowViewModel? targetRow)
    {
        if (!IsIncludedEditableChapter(sourceRow) || !IsIncludedEditableChapter(targetRow))
            return false;

        if (!_rows.Contains(sourceRow!) || !_rows.Contains(targetRow!))
            return false;

        return !string.Equals(sourceRow!.RelativePath, targetRow!.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsIncludedEditableChapter(GanttRowViewModel? row)
    {
        if (row?.IsEditable != true)
            return false;

        return _workspace.Nodes.Any(node =>
            node.Node.Kind == NodeKind.Chapter
            && !node.Node.IsExcluded
            && !node.Node.IsMissing
            && string.Equals(node.Node.RelativePath, row.RelativePath, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsImmediatelyBefore(GanttRowViewModel sourceRow, GanttRowViewModel targetRow)
    {
        var sourceIndex = _rows.IndexOf(sourceRow);
        var targetIndex = _rows.IndexOf(targetRow);
        return sourceIndex >= 0 && targetIndex >= 0 && sourceIndex + 1 == targetIndex;
    }

    private bool IsImmediatelyAfter(GanttRowViewModel sourceRow, GanttRowViewModel targetRow)
    {
        var sourceIndex = _rows.IndexOf(sourceRow);
        var targetIndex = _rows.IndexOf(targetRow);
        return sourceIndex >= 0 && targetIndex >= 0 && sourceIndex - 1 == targetIndex;
    }

    private async Task ExecuteWorkspaceReorderAsync(ReorderCardRequest request)
    {
        var selectionPath = request.RelativePath;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _workspace.ReorderChapterCommand.Execute(request).Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task.ConfigureAwait(false);
        RestoreSelectedRow(selectionPath);
    }

    private void RestoreSelectedRow(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var match = _rows.FirstOrDefault(row =>
            string.Equals(row.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            SelectedRow = match;
    }

    public string? GetCellCopyValue(GanttRowViewModel row, GanttEditableColumn column)
    {
        if (!row.IsEditable)
            return null;

        return column switch
        {
            GanttEditableColumn.StartDate => row.EditableStartDate?.ToString("yyyy-MM-dd"),
            GanttEditableColumn.EndDate => row.EditableEndDate?.ToString("yyyy-MM-dd"),
            GanttEditableColumn.Progress => (row.EditableProgressPercent ?? 0).ToString("0"),
            _ => null
        };
    }

    public async Task ApplyClipboardValueAsync(GanttRowViewModel row, GanttEditableColumn column, string raw)
    {
        if (!row.IsEditable)
            return;

        switch (column)
        {
            case GanttEditableColumn.StartDate:
                if (!GanttCellValueParser.TryParseDate(raw, out var start))
                {
                    _notificationService.ShowError("Invalid date format. Use yyyy-MM-dd.");
                    return;
                }
                row.EditableStartDate = start.ToDateTime(TimeOnly.MinValue);
                await SaveInlineCellAsync(row, GanttEditableColumn.StartDate).ConfigureAwait(false);
                break;

            case GanttEditableColumn.EndDate:
                if (!GanttCellValueParser.TryParseDate(raw, out var end))
                {
                    _notificationService.ShowError("Invalid date format. Use yyyy-MM-dd.");
                    return;
                }
                row.EditableEndDate = end.ToDateTime(TimeOnly.MinValue);
                await SaveInlineCellAsync(row, GanttEditableColumn.EndDate).ConfigureAwait(false);
                break;

            case GanttEditableColumn.Progress:
                if (!GanttCellValueParser.TryParseProgress(raw, out var progress))
                {
                    _notificationService.ShowError("Invalid progress value. Use 0–100.");
                    return;
                }
                row.EditableProgressPercent = progress;
                await SaveInlineCellAsync(row, GanttEditableColumn.Progress).ConfigureAwait(false);
                break;
        }
    }

    public async Task SaveInlineCellAsync(GanttRowViewModel row, GanttEditableColumn column)
    {
        if (!row.IsEditable)
            return;

        var chapterVm = _workspace.Nodes
            .FirstOrDefault(n => n.Node.RelativePath == row.RelativePath);

        if (chapterVm == null)
        {
            _notificationService.ShowError($"Could not find chapter '{row.Title}' to save edits.");
            return;
        }

        var start = row.EditableStartDate.HasValue
            ? DateOnly.FromDateTime(row.EditableStartDate.Value)
            : (DateOnly?)null;
        var end = row.EditableEndDate.HasValue
            ? DateOnly.FromDateTime(row.EditableEndDate.Value)
            : (DateOnly?)null;
        var status = row.EditableStatus;
        // Mapped statuses (e.g. a story's stored Polishing) edit the segment they display as.
        var phaseName = _workspace.Profile.DisplayStatus(status).ToString();
        var startStr = start?.ToString("yyyy-MM-dd");
        var endStr = end?.ToString("yyyy-MM-dd");
        var progress = Math.Clamp(row.EditableProgressPercent ?? 0.0, 0.0, 100.0);

        var (existingStatus, existingStart, existingEnd, existingProgress) = GetPersistedInlineValues(chapterVm, status);
        if (existingStatus == status
            && string.Equals(existingStart, startStr, StringComparison.Ordinal)
            && string.Equals(existingEnd, endStr, StringComparison.Ordinal)
            && Math.Abs(existingProgress - progress) < 0.0001)
        {
            return;
        }

        if (start.HasValue && end.HasValue && end.Value < start.Value)
        {
            _notificationService.ShowError("End date cannot be before start date.");
            return;
        }

        PhaseData? updated = null;
        await _workspace.MetadataStore.UpsertPhaseDataAsync(_workspace.WorkspaceRoot, chapterVm.Story, current =>
        {
            var basePhaseData = current ?? chapterVm.PhaseData ?? PhaseDataService.DefaultPhaseDataFor(_workspace.Profile);
            var schedule = new Dictionary<string, PhaseScheduleEntry>(
                basePhaseData.Schedule ?? new Dictionary<string, PhaseScheduleEntry>(),
                StringComparer.Ordinal);

            string? phaseStart = basePhaseData.PhaseStartDate;
            string? phaseEnd = basePhaseData.PhaseEndDate;

            if (IsPhaseStatus(status))
            {
                schedule.TryGetValue(phaseName, out var existing);

                var entry = new PhaseScheduleEntry
                {
                    StartDate = column == GanttEditableColumn.StartDate
                        ? startStr
                        : existing?.StartDate,
                    EndDate = column == GanttEditableColumn.EndDate
                        ? endStr
                        : existing?.EndDate,
                    Progress = column == GanttEditableColumn.Progress
                        ? progress
                        : existing?.Progress ?? 0.0
                };

                schedule[phaseName] = entry;

                phaseStart = entry.StartDate;
                phaseEnd = entry.EndDate;
            }
            else
            {
                if (column == GanttEditableColumn.StartDate)
                    phaseStart = startStr;
                if (column == GanttEditableColumn.EndDate)
                    phaseEnd = endStr;
            }

            updated = new PhaseData
            {
                Status = status,
                PhaseStartDate = phaseStart,
                PhaseEndDate = phaseEnd,
                Schedule = schedule
            };

            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
            chapterVm.ApplyPhaseData(updated);
    }

    private void SetEditingStatus(ChapterStatus status)
    {
        EditingStatus = status;
        if (EditingRow != null)
            LoadEditorValuesForStatus(EditingRow, status);
    }

    private void LoadEditorValuesForStatus(GanttRowViewModel row, ChapterStatus status)
    {
        var phaseName = _workspace.Profile.DisplayStatus(status).ToString();
        var seg = row.PhaseSegments.FirstOrDefault(s =>
            string.Equals(s.PhaseName, phaseName, StringComparison.Ordinal));

        EditingPhaseName = phaseName;
        EditStartDate = seg?.StartDate.HasValue == true
            ? seg.StartDate!.Value.ToDateTime(TimeOnly.MinValue)
            : null;
        EditEndDate = seg?.EndDate.HasValue == true
            ? seg.EndDate!.Value.ToDateTime(TimeOnly.MinValue)
            : null;
        EditProgressPercent = seg is null ? 0.0 : Math.Round(seg.Progress * 100.0, 0);
    }

    private void CancelEdit()
    {
        IsEditingDates = false;
        EditingRow = null;
        EditingColumn = GanttEditableColumn.None;
        EditingStatus = ChapterStatus.Outlining;
        EditingPhaseName = string.Empty;
        EditStartDate = null;
        EditEndDate = null;
        EditProgressPercent = null;
        EditPopupMargin = new Thickness(0);
        EditCellWidth = 0;
        EditCellHeight = 0;
    }

    private async Task CommitEditAsync()
    {
        var row = EditingRow;
        if (row == null)
            return;

        var chapterVm = _workspace.Nodes
            .FirstOrDefault(n => n.Node.RelativePath == row.RelativePath);

        if (chapterVm == null)
        {
            _notificationService.ShowError($"Could not find chapter '{row.Title}' to save edits.");
            return;
        }

        var start = EditStartDate.HasValue
            ? DateOnly.FromDateTime(EditStartDate.Value)
            : (DateOnly?)null;
        var end = EditEndDate.HasValue
            ? DateOnly.FromDateTime(EditEndDate.Value)
            : (DateOnly?)null;

        if (start.HasValue && end.HasValue && end.Value < start.Value)
        {
            _notificationService.ShowError("End date cannot be before start date.");
            return;
        }

        var startStr = start?.ToString("yyyy-MM-dd");
        var endStr = end?.ToString("yyyy-MM-dd");
        var progress = Math.Clamp(EditProgressPercent ?? 0.0, 0.0, 100.0);

        var editingStatus = EditingStatus;
        var phaseName = _workspace.Profile.DisplayStatus(editingStatus).ToString();

        PhaseData? updated = null;
        await _workspace.MetadataStore.UpsertPhaseDataAsync(_workspace.WorkspaceRoot, chapterVm.Story, current =>
        {
            var basePhaseData = current ?? chapterVm.PhaseData ?? PhaseDataService.DefaultPhaseDataFor(_workspace.Profile);
            var schedule = new Dictionary<string, PhaseScheduleEntry>(
                basePhaseData.Schedule ?? new Dictionary<string, PhaseScheduleEntry>(),
                StringComparer.Ordinal);

            string? phaseStart = basePhaseData.PhaseStartDate;
            string? phaseEnd = basePhaseData.PhaseEndDate;

            if (IsPhaseStatus(editingStatus))
            {
                schedule[phaseName] = new PhaseScheduleEntry
                {
                    StartDate = startStr,
                    EndDate = endStr,
                    Progress = progress
                };
                phaseStart = startStr;
                phaseEnd = endStr;
            }
            else
            {
                phaseStart = startStr;
                phaseEnd = endStr;
            }

            updated = new PhaseData
            {
                Status = editingStatus,
                PhaseStartDate = phaseStart,
                PhaseEndDate = phaseEnd,
                Schedule = schedule
            };

            return updated;
        }).ConfigureAwait(false);

        if (updated != null)
            chapterVm.ApplyPhaseData(updated);

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            CancelEdit();
        else
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(CancelEdit);
    }

    /// <summary>True when <paramref name="status"/> displays as one of the profile's Gantt phases.</summary>
    private bool IsPhaseStatus(ChapterStatus status) =>
        _workspace.Profile.IsTrackablePhase(_workspace.Profile.DisplayStatus(status));

    private (ChapterStatus Status, string? Start, string? End, double Progress) GetPersistedInlineValues(
        ChapterViewModel chapterVm,
        ChapterStatus status)
    {
        var phaseData = chapterVm.PhaseData ?? PhaseDataService.DefaultPhaseDataFor(_workspace.Profile);
        var phaseName = _workspace.Profile.DisplayStatus(status).ToString();

        if (IsPhaseStatus(status)
            && phaseData.Schedule?.TryGetValue(phaseName, out var existing) == true)
        {
            return (status, existing.StartDate, existing.EndDate, existing.Progress ?? 0.0);
        }

        return (phaseData.Status, phaseData.PhaseStartDate, phaseData.PhaseEndDate, 0.0);
    }

    private void RebuildRows()
    {
        _rows.Clear();

        var phaseSubscriptions = new CompositeDisposable();
        var nodes = _workspace.Nodes.ToList();

        var allChapters = nodes
            .Where(n => !n.Node.IsMissing && n.Node.Kind == NodeKind.Chapter)
            .ToList();
        var profile = _workspace.Profile;
        var bookRowData = BuildBookRollup(allChapters, profile);
        _rows.Add(new GanttRowViewModel(bookRowData, profile));

        for (int i = 0; i < nodes.Count; i++)
        {
            var vm = nodes[i];
            if (vm.Node.IsMissing) continue;

            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(ChapterViewModel.PhaseData))
                    RebuildRows();
            };
            vm.PropertyChanged += handler;
            phaseSubscriptions.Add(Disposable.Create(() => vm.PropertyChanged -= handler));

            GanttRowData rowData;
            if (vm.Node.Kind == NodeKind.Part)
            {
                var children = new List<ChapterViewModel>();
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    if (nodes[j].Node.Kind == NodeKind.Part) break;
                    children.Add(nodes[j]);
                }
                rowData = BuildPartRollup(vm.Node, children, profile);
            }
            else
            {
                rowData = GanttProjection.Project(vm.Node, vm.PhaseData, profile);
            }

            var rowVm = new GanttRowViewModel(rowData, profile);
            if (rowVm.IsChapter)
            {
                var captured = rowVm;
                phaseSubscriptions.Add(
                    rowVm.EditDatesCommand.Subscribe(_ => _rowEditRequested.OnNext(captured)));
            }

            _rows.Add(rowVm);
        }

        _phaseSubscriptions.Disposable = phaseSubscriptions;
    }

    private static GanttRowData BuildPartRollup(
        ChapterNode partNode, IReadOnlyList<ChapterViewModel> children, ProjectProfile profile)
    {
        DateOnly? minStart = null;
        DateOnly? maxEnd = null;
        int done = 0;
        int total = children.Count;

        foreach (var child in children)
        {
            var childRow = GanttProjection.Project(child.Node, child.PhaseData, profile);

            if (childRow.StartDate.HasValue &&
                (minStart is null || childRow.StartDate.Value < minStart.Value))
                minStart = childRow.StartDate;

            if (childRow.EndDate.HasValue &&
                (maxEnd is null || childRow.EndDate.Value > maxEnd.Value))
                maxEnd = childRow.EndDate;

            if (profile.DisplayStatus(childRow.Status) == ChapterStatus.Done)
                done++;
        }

        double completion = total > 0 ? (double)done / total : 0.0;
        bool isMissing = minStart is null || maxEnd is null;

        return new GanttRowData(
            RelativePath: partNode.RelativePath,
            Title: partNode.Title,
            Kind: partNode.Kind,
            Status: ChapterStatus.Outlining,
            StartDate: minStart,
            EndDate: maxEnd,
            IsMissingDates: isMissing,
            CompletionPercentage: completion);
    }

    private static GanttRowData BuildBookRollup(IReadOnlyList<ChapterViewModel> chapters, ProjectProfile profile)
    {
        DateOnly? minStart = null;
        DateOnly? maxEnd = null;
        int done = 0;
        int total = chapters.Count;

        foreach (var ch in chapters)
        {
            var row = GanttProjection.Project(ch.Node, ch.PhaseData, profile);

            foreach (var seg in row.PhaseSegments ?? Array.Empty<PhaseSegment>())
            {
                if (seg.StartDate.HasValue && (minStart is null || seg.StartDate.Value < minStart.Value))
                    minStart = seg.StartDate;
                if (seg.EndDate.HasValue && (maxEnd is null || seg.EndDate.Value > maxEnd.Value))
                    maxEnd = seg.EndDate;
            }

            if (row.StartDate.HasValue && (minStart is null || row.StartDate.Value < minStart.Value))
                minStart = row.StartDate;
            if (row.EndDate.HasValue && (maxEnd is null || row.EndDate.Value > maxEnd.Value))
                maxEnd = row.EndDate;

            if (profile.DisplayStatus(row.Status) == ChapterStatus.Done)
                done++;
        }

        double completion = total > 0 ? (double)done / total : 0.0;
        bool isMissing = minStart is null || maxEnd is null;

        return new GanttRowData(
            RelativePath: "__book__",
            Title: "BOOK",
            Kind: NodeKind.Part,
            Status: ChapterStatus.Outlining,
            StartDate: minStart,
            EndDate: maxEnd,
            IsMissingDates: isMissing,
            CompletionPercentage: completion,
            IsBook: true);
    }
}

/// <summary>One Gantt legend swatch: phase label and its colour.</summary>
public sealed record GanttLegendItem(string Label, ChapterStatus Status)
{
    /// <summary>The status colour in the current theme, as <c>#RRGGBB</c>.</summary>
    public string ColorHex => GanttRowViewModel.StatusToColorHex(Status);

    /// <summary>The status colour as a theme brush that follows theme changes (what the legend swatch binds to).</summary>
    public Avalonia.Media.IBrush Brush => GanttRowViewModel.StatusBrush(Status);
}
