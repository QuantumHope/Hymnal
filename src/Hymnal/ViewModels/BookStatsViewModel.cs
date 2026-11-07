using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Threading;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Immutable display record for a single bar in the word-count bar chart.
/// </summary>
public sealed class BarItemViewModel
{
    public string Title { get; init; } = "";
    public int WordCount { get; init; }
    public string WordCountDisplay { get; init; } = "";
    public double FillFraction { get; init; }
    public ChapterStatus Status { get; init; }
}

/// <summary>
/// Backs the context-sensitive Part/Book stats panel in the right sidebar.
/// Becomes visible when the user selects a Part node or the Book header;
/// feeds data to <see cref="Views.BookStatsView"/>.
/// </summary>
public sealed class BookStatsViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _workspace;
    private readonly WordCountHistoryService _historyService;
    private readonly TargetsService _targetsService;
    private CompositeDisposable _selectionDisposables = new();

    // ── Visibility ────────────────────────────────────────────────────────────

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private string _panelTitle = "BOOK STATS";
    public string PanelTitle
    {
        get => _panelTitle;
        private set => this.RaiseAndSetIfChanged(ref _panelTitle, value);
    }

    private string _contextTitle = "";
    public string ContextTitle
    {
        get => _contextTitle;
        private set => this.RaiseAndSetIfChanged(ref _contextTitle, value);
    }

    // ── Word count ────────────────────────────────────────────────────────────

    private int _totalWordCount;
    public int TotalWordCount
    {
        get => _totalWordCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalWordCount, value);
            this.RaisePropertyChanged(nameof(TotalWordCountDisplay));
        }
    }

    public string TotalWordCountDisplay => $"{TotalWordCount:N0} w";

    // ── Chart data ────────────────────────────────────────────────────────────

    private IReadOnlyList<StatusCount> _statusSummary = Array.Empty<StatusCount>();
    public IReadOnlyList<StatusCount> StatusSummary
    {
        get => _statusSummary;
        private set => this.RaiseAndSetIfChanged(ref _statusSummary, value);
    }

    private IReadOnlyList<BarItemViewModel> _barItems = Array.Empty<BarItemViewModel>();
    public IReadOnlyList<BarItemViewModel> BarItems
    {
        get => _barItems;
        private set => this.RaiseAndSetIfChanged(ref _barItems, value);
    }

    private string _barSectionLabel = "BY PART";
    public string BarSectionLabel
    {
        get => _barSectionLabel;
        private set => this.RaiseAndSetIfChanged(ref _barSectionLabel, value);
    }

    private IReadOnlyList<StackedHistoryPoint> _stackedHistoryItems = Array.Empty<StackedHistoryPoint>();
    public IReadOnlyList<StackedHistoryPoint> StackedHistoryItems
    {
        get => _stackedHistoryItems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _stackedHistoryItems, value);
            this.RaisePropertyChanged(nameof(HasStackedHistory));
        }
    }

    public bool HasStackedHistory => _stackedHistoryItems.Count > 0;

    private int? _totalTarget;
    public int? TotalTarget
    {
        get => _totalTarget;
        private set => this.RaiseAndSetIfChanged(ref _totalTarget, value);
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public BookStatsViewModel(WorkspaceViewModel workspace, WordCountHistoryService historyService, TargetsService targetsService)
    {
        _workspace = workspace;
        _historyService = historyService;
        _targetsService = targetsService;

        Disposables.Add(
            workspace.WhenAnyValue(x => x.SelectedNode, x => x.HasWorkspace, x => x.Profile)
                .Subscribe(t => OnSelectionChanged(t.Item1, t.Item2)));

        Disposables.Add(Disposable.Create(() => _selectionDisposables.Dispose()));
    }

    // ── Selection change handler ──────────────────────────────────────────────

    private void OnSelectionChanged(ChapterViewModel? selected, bool hasWorkspace)
    {
        _selectionDisposables.Dispose();
        _selectionDisposables = new CompositeDisposable();

        if (!hasWorkspace || !_workspace.Profile.ShowsBookStats)
        {
            IsVisible = false;
            return;
        }

        if (selected == null)
        {
            ActivateBookMode();
        }
        else if (selected.Node.Kind == NodeKind.Part)
        {
            ActivatePartMode(selected);
        }
        else
        {
            IsVisible = false;
        }
    }

    private void ActivateBookMode()
    {
        IsVisible = true;
        PanelTitle = _workspace.Profile.BookStatsTitle;
        ContextTitle = _workspace.WorkspaceName ?? _workspace.Profile.CollectionNoun;
        BarSectionLabel = _workspace.Profile.BookStatsBarLabel;

        TotalWordCount = _workspace.TotalWordCount;
        StatusSummary = _workspace.BookStatusSummary;
        RefreshBookBars();

        // Each chapter is tagged with its parent part name so history aggregates BY PART
        var chapters = new List<(string Uuid, string Title, ChapterStatus Status)>();
        string currentPartName = "";
        ChapterStatus currentPartStatus = ChapterStatus.Planned;
        foreach (var vm in _workspace.Nodes)
        {
            if (vm.Node.Kind == NodeKind.Part)
            {
                currentPartName = vm.Node.Title;
                currentPartStatus = DominantStatus(vm.PartStatusSummary);
            }
            else if (vm.Node.Kind == NodeKind.Chapter && !string.IsNullOrEmpty(vm.Uuid))
            {
                chapters.Add((vm.Uuid!, currentPartName, currentPartStatus));
            }
        }
        _ = LoadStackedHistoryAsync(chapters);

        _selectionDisposables.Add(
            _workspace.WhenAnyValue(x => x.TotalWordCount)
                .Subscribe(c =>
                {
                    TotalWordCount = c;
                    RefreshBookBars();
                }));

        _selectionDisposables.Add(
            _workspace.WhenAnyValue(x => x.BookStatusSummary)
                .Subscribe(s => StatusSummary = s));
    }

    private void ActivatePartMode(ChapterViewModel partVm)
    {
        IsVisible = true;
        PanelTitle = _workspace.Profile.PartStatsTitle;
        ContextTitle = partVm.Node.Title;
        BarSectionLabel = _workspace.Profile.PartStatsBarLabel;

        TotalWordCount = partVm.PartTotalWordCount;
        StatusSummary = partVm.PartStatusSummary;
        RefreshPartBars(partVm);

        var chapters = new List<(string Uuid, string Title, ChapterStatus Status)>();
        bool inPart = false;
        foreach (var vm in _workspace.Nodes)
        {
            if (ReferenceEquals(vm, partVm)) { inPart = true; continue; }
            if (!inPart) continue;
            if (vm.Node.Kind == NodeKind.Part) break;
            if (vm.Node.Kind == NodeKind.Chapter && !string.IsNullOrEmpty(vm.Uuid))
                chapters.Add((vm.Uuid!, vm.Node.Title, vm.Status));
        }
        _ = LoadStackedHistoryAsync(chapters);

        _selectionDisposables.Add(
            partVm.WhenAnyValue(x => x.PartTotalWordCount)
                .Subscribe(c =>
                {
                    TotalWordCount = c;
                    RefreshPartBars(partVm);
                }));

        _selectionDisposables.Add(
            partVm.WhenAnyValue(x => x.PartStatusSummary)
                .Subscribe(s => StatusSummary = s));

        _selectionDisposables.Add(
            _workspace.WhenAnyValue(x => x.TotalWordCount)
                .Subscribe(_ => RefreshPartBars(partVm)));
    }

    // ── Stacked history chart loading ─────────────────────────────────────────

    private async Task LoadStackedHistoryAsync(IReadOnlyList<(string Uuid, string Title, ChapterStatus Status)> chapters)
    {
        var workspaceRoot = _workspace.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot) || chapters.Count == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StackedHistoryItems = Array.Empty<StackedHistoryPoint>();
                TotalTarget = null;
            });
            return;
        }

        try
        {
            var allHistory = await _historyService.GetAllAsync(workspaceRoot).ConfigureAwait(false);
            var allTargets = await _targetsService.LoadAsync(workspaceRoot).ConfigureAwait(false);

            var uuidLookup = chapters.ToDictionary(c => c.Uuid, c => (c.Title, c.Status), StringComparer.Ordinal);

            // Stable ordered list of unique labels (part names in Book mode, chapter titles in Part mode)
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            var orderedLabels = chapters
                .Select(c => (c.Title, c.Status))
                .Where(x => seenLabels.Add(x.Title))
                .ToList();

            // Group history entries by date
            var byDate = allHistory
                .Where(e => uuidLookup.ContainsKey(e.Uuid))
                .GroupBy(e => e.Date)
                .Select(g =>
                {
                    // Max word count per UUID that day (idempotent writes)
                    var segMap = g.GroupBy(e => e.Uuid)
                        .ToDictionary(ug => ug.Key, ug => ug.Max(e => e.WordCount), StringComparer.Ordinal);

                    // Aggregate by label: sum chapters that share the same label (e.g. same part)
                    var labelTotals = chapters
                        .Where(c => segMap.ContainsKey(c.Uuid))
                        .GroupBy(c => c.Title, StringComparer.Ordinal)
                        .ToDictionary(
                            lg => lg.Key,
                            lg => lg.Sum(c => segMap[c.Uuid]),
                            StringComparer.Ordinal);

                    var segments = orderedLabels
                        .Where(x => labelTotals.ContainsKey(x.Title))
                        .Select(x => new HistorySegment(x.Title, labelTotals[x.Title], x.Status))
                        .ToList();

                    return new StackedHistoryPoint(g.Key, segments);
                })
                .Where(p => p.Segments.Count > 0)
                .OrderBy(p => p.Date)
                .ToList();

            // Sum MinWords targets for chapters that have one set
            int targetSum = chapters
                .Select(c => allTargets.TryGetValue(c.Uuid, out var t) ? (t.MinWords ?? 0) : 0)
                .Sum();
            int? totalTarget = targetSum > 0 ? targetSum : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StackedHistoryItems = byDate;
                TotalTarget = totalTarget;
            });
        }
        catch
        {
            // Non-fatal; chart stays empty
        }
    }

    // ── Bar data builders ─────────────────────────────────────────────────────

    private void RefreshBookBars()
    {
        var rawItems = new List<(string Title, int WordCount, ChapterStatus Status)>();
        foreach (var vm in _workspace.Nodes)
        {
            if (vm.Node.Kind == NodeKind.Part)
                rawItems.Add((vm.Node.Title, vm.PartTotalWordCount, DominantStatus(vm.PartStatusSummary)));
        }
        BarItems = BuildBarItems(rawItems);
    }

    private void RefreshPartBars(ChapterViewModel partVm)
    {
        var rawItems = new List<(string Title, int WordCount, ChapterStatus Status)>();
        bool inPart = false;
        foreach (var vm in _workspace.Nodes)
        {
            if (ReferenceEquals(vm, partVm)) { inPart = true; continue; }
            if (!inPart) continue;
            if (vm.Node.Kind == NodeKind.Part) break;
            if (vm.Node.Kind == NodeKind.Chapter)
                rawItems.Add((vm.Node.Title, vm.WordCount, vm.Status));
        }
        BarItems = BuildBarItems(rawItems);
    }

    private static IReadOnlyList<BarItemViewModel> BuildBarItems(
        List<(string Title, int WordCount, ChapterStatus Status)> raw)
    {
        if (raw.Count == 0) return Array.Empty<BarItemViewModel>();
        int max = raw.Max(r => r.WordCount);
        if (max == 0) max = 1;
        return raw.Select(r => new BarItemViewModel
        {
            Title = r.Title,
            WordCount = r.WordCount,
            WordCountDisplay = $"{r.WordCount:N0} w",
            FillFraction = (double)r.WordCount / max,
            Status = r.Status,
        }).ToList();
    }

    private ChapterStatus DominantStatus(IReadOnlyList<StatusCount> summary)
    {
        var profile = _workspace.Profile;
        if (summary.Count == 0) return ChapterStatus.Planned;
        foreach (var option in profile.VisibleStatuses)
        {
            if (summary.Any(s => profile.DisplayStatus(s.Status) == option.Value && s.Count > 0))
                return option.Value;
        }
        return summary[0].Status;
    }
}
