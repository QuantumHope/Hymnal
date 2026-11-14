using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.Theming;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// Read-only custom Gantt chart control.
/// Accepts a list of <see cref="GanttRowViewModel"/> rows and renders a time axis with
/// per-row phase segments (one coloured bar per authoring phase that has dates set).
/// Part rows render as section dividers with a rollup progress bar.
/// All drawing exceptions are swallowed silently so the view never crashes the host.
/// </summary>
public sealed class GanttCanvas : Control
{
    public sealed class GanttCellEditRequestedEventArgs : EventArgs
    {
        public GanttRowViewModel Row { get; }
        public GanttEditableColumn Column { get; }
        public Rect CellBounds { get; }

        public GanttCellEditRequestedEventArgs(
            GanttRowViewModel row,
            GanttEditableColumn column,
            Rect cellBounds)
        {
            Row = row;
            Column = column;
            CellBounds = cellBounds;
        }
    }

    public sealed class GanttRowReorderRequestedEventArgs : EventArgs
    {
        public GanttRowViewModel SourceRow { get; }
        public GanttRowViewModel TargetRow { get; }
        public bool DropBeforeTarget { get; }

        public GanttRowReorderRequestedEventArgs(
            GanttRowViewModel sourceRow,
            GanttRowViewModel targetRow,
            bool dropBeforeTarget)
        {
            SourceRow = sourceRow;
            TargetRow = targetRow;
            DropBeforeTarget = dropBeforeTarget;
        }
    }

    public event EventHandler<GanttCellEditRequestedEventArgs>? CellEditRequested;
    public event EventHandler<GanttRowReorderRequestedEventArgs>? RowReorderRequested;

    // ── Bar hover hit-testing ─────────────────────────────────────────────────

    /// <summary>One hittable bar region produced during Render().</summary>
    private sealed record GanttBarHitInfo(
        GanttRowViewModel Row,
        PhaseSegment HoveredSegment,
        Rect ScreenBounds);

    private List<GanttBarHitInfo> _barHitRegions = [];
    private GanttBarHitInfo? _currentHitInfo;

    // ── ShowTable property ────────────────────────────────────────────────────

    public static readonly StyledProperty<bool> ShowTableProperty =
        AvaloniaProperty.Register<GanttCanvas, bool>(nameof(ShowTable), true);

    public bool ShowTable
    {
        get => GetValue(ShowTableProperty);
        set => SetValue(ShowTableProperty, value);
    }

    // ── Layout constants ──────────────────────────────────────────────────────

    private const double TitleColumnWidth    = 220.0;
    private const double StatusColumnWidth   = 100.0;
    private const double StartColumnWidth    = 96.0;
    private const double EndColumnWidth      = 96.0;
    private const double ProgressColumnWidth = 80.0;

    private const double StatusColumnLeft   = TitleColumnWidth;
    private const double StartColumnLeft    = StatusColumnLeft + StatusColumnWidth;
    private const double EndColumnLeft      = StartColumnLeft + StartColumnWidth;
    private const double ProgressColumnLeft = EndColumnLeft + EndColumnWidth;
    private const double TimelineLeft       = ProgressColumnLeft + ProgressColumnWidth;

    private const double HeaderHeight     = 36.0;
    private const double RowHeight        = 28.0;
    private const double BoxVPadding      = 5.0;
    private const double MinTimelineWidth = 1100.0;

    // ── Brushes / pens ────────────────────────────────────────────────────────

    // Colours come from the theme and are read at draw time (a theme switch redraws, see the constructor). The
    // fallbacks are Synthwave's original values, used when no theme is loaded.
    private static IBrush ThemeBrush(string key, string fallback) => ThemeBrushes.Get(key, fallback);
    private static Color ThemeColor(string key, string fallback) => ThemeBrushes.Color(key, fallback);
    private static IBrush Tint(Color c, byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    // Axis / grid
    private static IBrush BackgroundBrush  => ThemeBrush("Cora.Surface.Void", "#0D0B22");
    private static IBrush HeaderBgBrush    => ThemeBrush("Cora.Surface.Header", "#120932");
    private static IBrush LabelColumnBrush => ThemeBrush("Cora.Surface.Panel", "#0F0828");
    private static IBrush GridLineBrush    => Tint(ThemeColor("Cora.Text", "#FFFFFF"), 0x28);
    private static IBrush AxisTextBrush    => ThemeBrush("Cora.Text.Secondary", "#94A3B8");
    private static IBrush LabelTextBrush   => ThemeBrush("Cora.Text", "#CBD5E1");
    private static IBrush LabelDimBrush    => ThemeBrush("Cora.Text.Disabled", "#64748B");
    private static IBrush PartRowBgBrush   => Tint(ThemeColor("Cora.Accent.Store", "#9D4EDD"), 0x18);
    private static IBrush EmptyStateBrush  => ThemeBrush("Cora.Text.Disabled", "#475569");
    private static Pen    SeparatorPen     => new(Tint(ThemeColor("Cora.Accent.Store", "#9D4EDD"), 0x50), 1.0);
    private static Pen    GridLinePen      => new(GridLineBrush, 0.5);

    // Per-phase fill colours — keyed by the canonical phase name string.
    private static readonly Dictionary<string, (string Key, string Fallback)> PhaseBarKeys = new()
    {
        ["Outlining"] = ("Cora.Text.Secondary", "#9589B0"),
        ["Drafting"]  = ("Cora.Accent.Retrieve", "#38BDF8"),
        ["Editing"]   = ("Cora.Accent.Store", "#9D4EDD"),
        ["Polishing"] = ("Cora.Accent.Create", "#F5C842"),
        ["Reviewing"] = ("Cora.Accent.Merge", "#E91E8C"),
    };

    private static Color PhaseColor(string phaseName) =>
        PhaseBarKeys.TryGetValue(phaseName, out var k)
            ? ThemeColor(k.Key, k.Fallback)
            : ThemeColor("Cora.Text.Disabled", "#64748B");

    // Part rollup colours
    private static IBrush PartRollupTrackBrush => Tint(ThemeColor("Cora.Text.Disabled", "#475569"), 0x60);
    private static IBrush PartRollupFillBrush  => ThemeBrush("Cora.Accent.Success", "#34D399");
    private static Pen    PartRollupStrokePen  => new(Tint(ThemeColor("Cora.Accent.Success", "#34D399"), 0xA0), 1.0);
    // Rollup % labels sit over both the track and the fill, so they use body text rather than icon ink.
    private static IBrush RollupLabelBrush     => ThemeBrush("Cora.Text", "#FFFFFF");

    // ── AvaloniaProperty: ItemHeader ──────────────────────────────────────────

    /// <summary>The first column's header: CHAPTER, SCENE or STORY (the project's profile).</summary>
    public static readonly StyledProperty<string> ItemHeaderProperty =
        AvaloniaProperty.Register<GanttCanvas, string>(nameof(ItemHeader), "CHAPTER");

    public string ItemHeader
    {
        get => GetValue(ItemHeaderProperty);
        set => SetValue(ItemHeaderProperty, value);
    }

    // ── AvaloniaProperty: Rows ────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<GanttRowViewModel>?> RowsProperty =
        AvaloniaProperty.Register<GanttCanvas, IReadOnlyList<GanttRowViewModel>?>(
            nameof(Rows));

    public IReadOnlyList<GanttRowViewModel>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private INotifyCollectionChanged? _trackedCollection;
    private Point? _rowDragStartPoint;
    private GanttRowReorderRequestedEventArgs? _previewReorder;

    // ── Constructor ───────────────────────────────────────────────────────────

    public GanttCanvas()
    {
        ToolTip.SetShowDelay(this, 300);
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        ToolTip.SetHorizontalOffset(this, 24);
        // A theme switch swaps the app's resources; redraw so the tinted colours follow it.
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    // ── Property change / collection watching ────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            if (_trackedCollection != null)
                _trackedCollection.CollectionChanged -= OnRowsCollectionChanged;

            _trackedCollection = Rows as INotifyCollectionChanged;
            if (_trackedCollection != null)
                _trackedCollection.CollectionChanged += OnRowsCollectionChanged;

            InvalidateVisual();
        }
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    // ── Pointer hit-test ──────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        try
        {
            _rowDragStartPoint = null;

            var rows = Rows;
            if (rows == null || rows.Count == 0) return;

            var pos = e.GetPosition(this);
            if (pos.Y < HeaderHeight) return;

            int rowIndex = (int)((pos.Y - HeaderHeight) / RowHeight);
            if (rowIndex < 0 || rowIndex >= rows.Count) return;

            var row = rows[rowIndex];
            var editColumn = GetEditableColumnAtX(pos.X);
            bool clickedEditableTableCell = ShowTable && editColumn != GanttEditableColumn.None;

            if (row.IsChapter && !row.IsBook && clickedEditableTableCell)
            {
                row.PendingEditColumn = editColumn;
                var cellRect = GetCellBounds(rowIndex, editColumn);
                CellEditRequested?.Invoke(this, new GanttCellEditRequestedEventArgs(row, editColumn, cellRect));
                row.EditDatesCommand.Execute().Subscribe();
                return;
            }

            _rowDragStartPoint = row.IsEditable ? pos : null;
        }
        catch { /* swallow */ }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        try
        {
            if (_rowDragStartPoint is { } startPoint)
            {
                TryRaiseRowReorderIntent(startPoint, e.GetPosition(this));
            }
        }
        catch { /* swallow */ }
        finally
        {
            _rowDragStartPoint = null;
            ClearRowReorderPreview();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        try
        {
            var pos = e.GetPosition(this);

            if (_rowDragStartPoint is { } dragStart)
                UpdateRowReorderPreview(dragStart, pos);

            GanttBarHitInfo? hit = null;

            foreach (var region in _barHitRegions)
            {
                if (region.ScreenBounds.Contains(pos))
                {
                    hit = region;
                    break;
                }
            }

            // Only rebuild tooltip when the hovered bar changes.
            bool changed = hit?.Row != _currentHitInfo?.Row
                || hit?.HoveredSegment?.PhaseName != _currentHitInfo?.HoveredSegment?.PhaseName;

            if (changed)
            {
                _currentHitInfo = hit;
                ToolTip.SetTip(this, hit != null ? BuildTooltipContent(hit) : null);
            }
        }
        catch { /* swallow */ }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _currentHitInfo = null;
        ToolTip.SetTip(this, null);

        if (_rowDragStartPoint == null)
            ClearRowReorderPreview();
    }

    // ── MeasureOverride ───────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        int rowCount = Rows?.Count ?? 0;
        double desiredHeight = HeaderHeight + rowCount * RowHeight;

        double minWidth = ShowTable ? TimelineLeft + MinTimelineWidth : MinTimelineWidth;
        double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Max(availableSize.Width, minWidth)
            : minWidth;

        double height = double.IsFinite(desiredHeight) && desiredHeight > 0
            ? desiredHeight
            : HeaderHeight;

        return new Size(width, height);
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        try
        {
            var rows = Rows;
            bool showTable = ShowTable;
            double w = Bounds.Width;
            double logicalWidth = showTable ? w : (w + TimelineLeft);
            double h = Bounds.Height;

            // ── Background ────────────────────────────────────────────────────
            ctx.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));
            if (showTable)
                ctx.FillRectangle(LabelColumnBrush, new Rect(0, 0, TimelineLeft, h));
            ctx.FillRectangle(HeaderBgBrush,    new Rect(0, 0, w, HeaderHeight));

            IDisposable? transformScope = null;
            if (!showTable)
                transformScope = ctx.PushTransform(Matrix.CreateTranslation(-TimelineLeft, 0));

            if (showTable)
                DrawColumnHeaders(ctx, ItemHeader);

            if (rows == null || rows.Count == 0)
            {
                transformScope?.Dispose();
                DrawEmptyState(ctx, w, h, "Nothing to schedule yet.");
                return;
            }

            // ── Compute date range across all phase segments ──────────────────
            var allSegments = rows
                .Where(r => r.IsChapter)
                .SelectMany(r => r.PhaseSegments)
                .ToList();

            var allDates = allSegments
                .SelectMany(s => new[] { s.StartDate, s.EndDate })
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();

            // Fall back: also check row-level StartDate/EndDate (part rows).
            var rowDates = rows
                .SelectMany(r => new[] { r.StartDate, r.EndDate })
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();

            var combinedDates = allDates.Concat(rowDates).ToList();

            bool hasDateRange = combinedDates.Count > 0;

            DateOnly axisStart = hasDateRange
                ? combinedDates.Min()
                : DateOnly.FromDateTime(DateTime.Today);

            DateOnly axisEnd = hasDateRange
                ? combinedDates.Max()
                : DateOnly.FromDateTime(DateTime.Today.AddMonths(6));

            // Expand axis: start at the beginning of the month, end two months past.
            axisStart = new DateOnly(axisStart.Year, axisStart.Month, 1);
            axisEnd   = new DateOnly(axisEnd.Year, axisEnd.Month, 1).AddMonths(2);

            double chartWidth = Math.Max(0, logicalWidth - TimelineLeft);

            // ── Draw time axis ────────────────────────────────────────────────
            DrawTimeAxis(ctx, axisStart, axisEnd, chartWidth, logicalWidth, h, showTable);

            // ── Draw rows, accumulating bar hit regions ───────────────────────
            var hitRegions = new List<GanttBarHitInfo>();

            for (int i = 0; i < rows.Count; i++)
            {
                double rowY = HeaderHeight + i * RowHeight;
                DrawRow(ctx, rows[i], i, rowY, axisStart, axisEnd, chartWidth, showTable, hitRegions);
            }

            DrawRowReorderPreview(ctx, rows, logicalWidth);

            _barHitRegions = hitRegions;

            transformScope?.Dispose();
        }
        catch
        {
            // Swallow all rendering exceptions — must never crash the host.
        }
    }

    private static void DrawColumnHeaders(DrawingContext ctx, string itemHeader)
    {
        try
        {
            DrawHeaderText(ctx, itemHeader, 8);
            DrawHeaderText(ctx, "STATUS", StatusColumnLeft + 6);
            DrawHeaderText(ctx, "START", StartColumnLeft + 6);
            DrawHeaderText(ctx, "END", EndColumnLeft + 6);
            DrawHeaderText(ctx, "PROGRESS", ProgressColumnLeft + 6);

            ctx.DrawLine(SeparatorPen, new Point(TitleColumnWidth, 0), new Point(TitleColumnWidth, HeaderHeight));
            ctx.DrawLine(SeparatorPen, new Point(StatusColumnLeft + StatusColumnWidth, 0), new Point(StatusColumnLeft + StatusColumnWidth, HeaderHeight));
            ctx.DrawLine(SeparatorPen, new Point(StartColumnLeft + StartColumnWidth, 0), new Point(StartColumnLeft + StartColumnWidth, HeaderHeight));
            ctx.DrawLine(SeparatorPen, new Point(EndColumnLeft + EndColumnWidth, 0), new Point(EndColumnLeft + EndColumnWidth, HeaderHeight));
            ctx.DrawLine(SeparatorPen, new Point(ProgressColumnLeft + ProgressColumnWidth, 0), new Point(ProgressColumnLeft + ProgressColumnWidth, HeaderHeight));
        }
        catch { /* swallow */ }
    }

    private static void DrawHeaderText(DrawingContext ctx, string text, double x)
    {
        var ft = MakeFormattedText(text, 9.0, AxisTextBrush);
        ctx.DrawText(ft, new Point(x, (HeaderHeight - ft.Height) / 2));
    }

    private static void DrawTimeAxis(
        DrawingContext ctx,
        DateOnly start, DateOnly end,
        double chartWidth, double totalWidth, double totalHeight, bool showTable)
    {
        try
        {
            var current = start;
            while (current <= end)
            {
                double x = TimelineLeft + DateToX(current, start, end, chartWidth);

                ctx.DrawLine(GridLinePen, new Point(x, HeaderHeight), new Point(x, totalHeight));

                // Month label lives only in the header band, starting 3 px past the grid line.
                string label = current.ToString("MMM yy");
                var ft = MakeFormattedText(label, 9.0, AxisTextBrush);
                double labelY = (HeaderHeight - ft.Height) / 2;
                ctx.DrawText(ft, new Point(x + 3, labelY));

                current = current.AddMonths(1);
            }

            if (showTable)
            {
                // Vertical separators: table | timeline.
                ctx.DrawLine(SeparatorPen, new Point(TitleColumnWidth, 0), new Point(TitleColumnWidth, totalHeight));
                ctx.DrawLine(SeparatorPen, new Point(StatusColumnLeft + StatusColumnWidth, 0), new Point(StatusColumnLeft + StatusColumnWidth, totalHeight));
                ctx.DrawLine(SeparatorPen, new Point(StartColumnLeft + StartColumnWidth, 0), new Point(StartColumnLeft + StartColumnWidth, totalHeight));
                ctx.DrawLine(SeparatorPen, new Point(EndColumnLeft + EndColumnWidth, 0), new Point(EndColumnLeft + EndColumnWidth, totalHeight));
                ctx.DrawLine(SeparatorPen, new Point(ProgressColumnLeft + ProgressColumnWidth, 0), new Point(ProgressColumnLeft + ProgressColumnWidth, totalHeight));
            }
            ctx.DrawLine(SeparatorPen, new Point(TimelineLeft,     0), new Point(TimelineLeft,     totalHeight));
        }
        catch { /* swallow */ }
    }

    private static void DrawRow(
        DrawingContext ctx,
        GanttRowViewModel row,
        int rowIndex,
        double rowY,
        DateOnly axisStart, DateOnly axisEnd,
        double chartWidth,
        bool showTable,
        List<GanttBarHitInfo> hitRegions)
    {
        try
        {
            bool isEven = rowIndex % 2 == 0;
            if (isEven)
            {
                ctx.FillRectangle(
                    Tint(ThemeColor("Cora.Text", "#FFFFFF"), 0x0A),
                    new Rect(TimelineLeft, rowY, chartWidth, RowHeight));
            }

            if (row.IsBook)
            {
                DrawBookRow(ctx, row, rowY, axisStart, axisEnd, chartWidth);
                return;
            }

            if (row.IsPart)
            {
                DrawPartRow(ctx, row, rowY, axisStart, axisEnd, chartWidth);
                return;
            }

            // ── Title column ──────────────────────────────────────────────────
            bool hasAnyDate = row.PhaseSegments.Any(s => s.StartDate.HasValue || s.EndDate.HasValue);
            var labelBrush  = hasAnyDate ? LabelTextBrush : LabelDimBrush;
            var labelFt     = MakeFormattedText(
                TruncateLabel(row.Title, TitleColumnWidth - 16),
                11.5, labelBrush);
            ctx.DrawText(labelFt, new Point(8, rowY + (RowHeight - labelFt.Height) / 2));

            // ── Table cells (status/start/end/progress) ─────────────────────
            var currentSeg = row.CurrentSegment;

            var startText = currentSeg?.StartDate?.ToString("yyyy-MM-dd")
                ?? row.StartDate?.ToString("yyyy-MM-dd")
                ?? "--";
            var endText = currentSeg?.EndDate?.ToString("yyyy-MM-dd")
                ?? row.EndDate?.ToString("yyyy-MM-dd")
                ?? "--";

            bool hasProgress = currentSeg != null && currentSeg.Progress > 0;
            var progressText = hasProgress
                ? $"{Math.Round(currentSeg!.Progress * 100.0):0}%"
                : "--";

            var statusBrush = new SolidColorBrush(StatusToColor(row.DisplayStatus));
            DrawCellText(ctx, row.StatusLabel, StatusColumnLeft, StatusColumnWidth, rowY, 10.5, statusBrush);
            DrawCellText(ctx, startText, StartColumnLeft, StartColumnWidth, rowY, 10.0, labelBrush);
            DrawCellText(ctx, endText, EndColumnLeft, EndColumnWidth, rowY, 10.0, labelBrush);
            DrawCellText(ctx, progressText, ProgressColumnLeft, ProgressColumnWidth, rowY, 10.0, labelBrush);

            // ── Phase segments on the timeline ────────────────────────────────
            // X offset to convert from logical drawing coords to screen coords.
            double screenOffsetX = showTable ? 0.0 : -TimelineLeft;

            foreach (var seg in row.PhaseSegments)
            {
                if (!seg.StartDate.HasValue && !seg.EndDate.HasValue)
                    continue;  // phase not scheduled — skip

                var barColor = PhaseColor(seg.PhaseName);

                // If only one date is set, draw a thin marker line instead of a bar.
                if (!seg.StartDate.HasValue || !seg.EndDate.HasValue)
                {
                    var markerDate = seg.StartDate ?? seg.EndDate!.Value;
                    double mx = TimelineLeft + DateToX(markerDate, axisStart, axisEnd, chartWidth);
                    ctx.DrawLine(
                        new Pen(new SolidColorBrush(barColor), 1.5),
                        new Point(mx, rowY + BoxVPadding),
                        new Point(mx, rowY + RowHeight - BoxVPadding));

                    // Add a small hit region around the marker line.
                    double screenMx = mx + screenOffsetX;
                    hitRegions.Add(new GanttBarHitInfo(
                        row, seg,
                        new Rect(screenMx - 4, rowY + BoxVPadding, 8, RowHeight - BoxVPadding * 2)));
                    continue;
                }

                double boxLeft  = TimelineLeft + DateToX(seg.StartDate.Value, axisStart, axisEnd, chartWidth);
                double boxRight = TimelineLeft + DateToX(seg.EndDate.Value,   axisStart, axisEnd, chartWidth);
                double boxW     = Math.Max(boxRight - boxLeft, 3.0);
                double boxH     = RowHeight - BoxVPadding * 2;
                double boxTop   = rowY + BoxVPadding;

                var fillBrush   = new SolidColorBrush(Color.FromArgb(0xCC, barColor.R, barColor.G, barColor.B));
                var strokeBrush = new SolidColorBrush(barColor);

                // Progress fill clipped inside the bar.
                if (seg.Progress > 0 && boxW > 4)
                {
                    var trackRect = new Rect(boxLeft, boxTop, boxW, boxH);
                    ctx.DrawRectangle(fillBrush, new Pen(strokeBrush, 1.0), trackRect, 3, 3);

                    double fillW = Math.Clamp(seg.Progress * boxW, 0, boxW);
                    var brightFill = new SolidColorBrush(Color.FromArgb(0xFF, barColor.R, barColor.G, barColor.B));
                    using (ctx.PushClip(trackRect))
                    {
                        ctx.FillRectangle(brightFill, new Rect(boxLeft, boxTop, fillW, boxH));
                    }
                }
                else
                {
                    ctx.DrawRectangle(fillBrush, new Pen(strokeBrush, 1.0),
                        new Rect(boxLeft, boxTop, boxW, boxH), 3, 3);
                }

                // Bar label: show progress % (the bar colour already conveys the phase).
                if (boxW > 28)
                {
                    // Text on the accent-coloured bar.
                    var inkBrush = ThemeBrush("Cora.IconInk", "#FFFFFF");
                    string label = $"{(int)Math.Round(seg.Progress * 100)}%";
                    var ft = MakeFormattedText(label, 8.5, inkBrush);
                    if (ft.Width + 4 < boxW)
                    {
                        ctx.DrawText(ft,
                            new Point(boxLeft + (boxW - ft.Width) / 2,
                                      boxTop  + (boxH  - ft.Height) / 2));
                    }
                }

                // Record hit region in screen coordinates for hover tooltip.
                hitRegions.Add(new GanttBarHitInfo(
                    row, seg,
                    new Rect(boxLeft + screenOffsetX, boxTop, boxW, boxH)));
            }
        }
        catch { /* swallow */ }
    }

    private static void DrawBookRow(
        DrawingContext ctx,
        GanttRowViewModel row,
        double rowY,
        DateOnly axisStart, DateOnly axisEnd,
        double chartWidth)
    {
        try
        {
            // Prominent background strip.
            var bookBgBrush = Tint(ThemeColor("Cora.Accent.Store", "#9D4EDD"), 0x28);
            ctx.FillRectangle(bookBgBrush,
                new Rect(0, rowY, TimelineLeft + chartWidth, RowHeight));

            ctx.DrawLine(SeparatorPen,
                new Point(0, rowY),
                new Point(TimelineLeft + chartWidth, rowY));
            ctx.DrawLine(SeparatorPen,
                new Point(0, rowY + RowHeight),
                new Point(TimelineLeft + chartWidth, rowY + RowHeight));

            var ft = MakeFormattedText(row.Title, 10.0,
                ThemeBrush("Cora.Accent.Store.Hot", "#C4B5FD"));
            ctx.DrawText(ft, new Point(8, rowY + (RowHeight - ft.Height) / 2));

            DrawCellText(ctx, "Rollup", StatusColumnLeft, StatusColumnWidth, rowY, 10.0, LabelTextBrush);
            DrawCellText(ctx, row.StartDate?.ToString("yyyy-MM-dd") ?? "--", StartColumnLeft, StartColumnWidth, rowY, 10.0, LabelTextBrush);
            DrawCellText(ctx, row.EndDate?.ToString("yyyy-MM-dd") ?? "--", EndColumnLeft, EndColumnWidth, rowY, 10.0, LabelTextBrush);
            DrawCellText(ctx, $"{Math.Round(row.CompletionPercentage * 100.0):0}%", ProgressColumnLeft, ProgressColumnWidth, rowY, 10.0, LabelTextBrush);

            // Rollup bar — same as part row.
            if (!row.IsMissingDates && row.StartDate.HasValue && row.EndDate.HasValue)
            {
                double boxLeft  = TimelineLeft + DateToX(row.StartDate.Value, axisStart, axisEnd, chartWidth);
                double boxRight = TimelineLeft + DateToX(row.EndDate.Value,   axisStart, axisEnd, chartWidth);
                double boxW     = Math.Max(boxRight - boxLeft, 4.0);
                double boxH     = RowHeight - BoxVPadding * 2;
                double boxTop   = rowY + BoxVPadding;

                var trackRect = new Rect(boxLeft, boxTop, boxW, boxH);
                ctx.DrawRectangle(PartRollupTrackBrush, PartRollupStrokePen, trackRect, 3, 3);

                double fillW = Math.Max(Math.Min(row.CompletionPercentage * boxW, boxW), 0.0);
                if (fillW > 1.0)
                {
                    using (ctx.PushClip(trackRect))
                    {
                        ctx.FillRectangle(PartRollupFillBrush, new Rect(boxLeft, boxTop, fillW, boxH));
                    }
                }

                if (boxW > 36)
                {
                    string pctLabel = $"{(int)Math.Round(row.CompletionPercentage * 100)}%";
                    var pctFt = MakeFormattedText(pctLabel, 8.5, RollupLabelBrush);
                    if (pctFt.Width + 4 < boxW)
                    {
                        ctx.DrawText(pctFt,
                            new Point(boxLeft + (boxW - pctFt.Width) / 2,
                                      boxTop  + (boxH  - pctFt.Height) / 2));
                    }
                }
            }
        }
        catch { /* swallow */ }
    }

    private static void DrawPartRow(
        DrawingContext ctx,
        GanttRowViewModel row,
        double rowY,
        DateOnly axisStart, DateOnly axisEnd,
        double chartWidth)
    {
        try
        {
            ctx.FillRectangle(PartRowBgBrush,
                new Rect(0, rowY, TimelineLeft + chartWidth, RowHeight));

            ctx.DrawLine(SeparatorPen,
                new Point(0, rowY),
                new Point(TimelineLeft + chartWidth, rowY));

            var ft = MakeFormattedText(row.Title.ToUpperInvariant(), 9.5, LabelDimBrush);
            ctx.DrawText(ft, new Point(8, rowY + (RowHeight - ft.Height) / 2));

            DrawCellText(ctx, "Rollup", StatusColumnLeft, StatusColumnWidth, rowY, 10.0, LabelDimBrush);
            DrawCellText(ctx, row.StartDate?.ToString("yyyy-MM-dd") ?? "--", StartColumnLeft, StartColumnWidth, rowY, 10.0, LabelDimBrush);
            DrawCellText(ctx, row.EndDate?.ToString("yyyy-MM-dd") ?? "--", EndColumnLeft, EndColumnWidth, rowY, 10.0, LabelDimBrush);
            DrawCellText(ctx, $"{Math.Round(row.CompletionPercentage * 100.0):0}%", ProgressColumnLeft, ProgressColumnWidth, rowY, 10.0, LabelDimBrush);

            // Rollup span across all child dates.
            if (!row.IsMissingDates && row.StartDate.HasValue && row.EndDate.HasValue)
            {
                double boxLeft  = TimelineLeft + DateToX(row.StartDate.Value, axisStart, axisEnd, chartWidth);
                double boxRight = TimelineLeft + DateToX(row.EndDate.Value,   axisStart, axisEnd, chartWidth);
                double boxW     = Math.Max(boxRight - boxLeft, 4.0);
                double boxH     = RowHeight - BoxVPadding * 2;
                double boxTop   = rowY + BoxVPadding;

                var trackRect = new Rect(boxLeft, boxTop, boxW, boxH);
                ctx.DrawRectangle(PartRollupTrackBrush, PartRollupStrokePen, trackRect, 3, 3);

                double fillW = Math.Max(Math.Min(row.CompletionPercentage * boxW, boxW), 0.0);
                if (fillW > 1.0)
                {
                    using (ctx.PushClip(trackRect))
                    {
                        ctx.FillRectangle(PartRollupFillBrush, new Rect(boxLeft, boxTop, fillW, boxH));
                    }
                }

                if (boxW > 36)
                {
                    string pctLabel = $"{(int)Math.Round(row.CompletionPercentage * 100)}%";
                    var pctFt = MakeFormattedText(pctLabel, 8.5, RollupLabelBrush);
                    if (pctFt.Width + 4 < boxW)
                    {
                        ctx.DrawText(pctFt,
                            new Point(boxLeft + (boxW - pctFt.Width) / 2,
                                      boxTop  + (boxH  - pctFt.Height) / 2));
                    }
                }
            }
        }
        catch { /* swallow */ }
    }

    private static void DrawEmptyState(DrawingContext ctx, double w, double h, string message)
    {
        try
        {
            var ft = MakeFormattedText(message, 13.0, EmptyStateBrush);
            ctx.DrawText(ft, new Point((w - ft.Width) / 2, h / 2 - ft.Height / 2));
        }
        catch { /* swallow */ }
    }

    // ── Tooltip content builder ───────────────────────────────────────────────

    private static Control BuildTooltipContent(GanttBarHitInfo hit)
    {
        var row = hit.Row;
        var hoveredSeg = hit.HoveredSegment;

        var tooltipBg     = ThemeBrush("Cora.Surface.Elevated", "#160E30");
        var borderBrush   = ThemeBrush("Cora.Border.Input", "#3D2B6E");
        var titleBrush    = ThemeBrush("Cora.Text", "#E2DDFF");
        var dimBrush      = ThemeBrush("Cora.Text.Secondary", "#94A3B8");
        var mutedBrush    = ThemeBrush("Cora.Text.Disabled", "#64748B");
        var contentBrush  = ThemeBrush("Cora.Text", "#CBD5E1");
        var ruleBrush     = ThemeBrush("Cora.Border.Subtle", "#2D1B5E");

        var statusColor   = StatusToColor(row.DisplayStatus);

        var container = new StackPanel { Spacing = 5, Margin = new Thickness(10, 8, 10, 10) };

        // ── Chapter title ─────────────────────────────────────────────────────
        container.Children.Add(new TextBlock
        {
            Text       = row.Title,
            FontSize   = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = titleBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth   = 270
        });

        // ── Current status ────────────────────────────────────────────────────
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        statusRow.Children.Add(new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center
        });
        statusRow.Children.Add(new TextBlock
        {
            Text = row.StatusLabel,
            FontSize = 11,
            Foreground = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center
        });
        container.Children.Add(statusRow);

        // ── Separator ─────────────────────────────────────────────────────────
        container.Children.Add(new Border { Height = 1, Background = ruleBrush, Margin = new Thickness(0, 1, 0, 1) });

        // ── Phase schedule table ──────────────────────────────────────────────
        var phases = row.PhaseSegments
            .Where(s => s.StartDate.HasValue || s.EndDate.HasValue)
            .ToList();

        if (phases.Count > 0)
        {
            // Column header row
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("92")));
            headerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("70")));
            headerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("70")));
            headerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("32")));

            AddHeaderCell(headerRow, "PHASE", 0, mutedBrush);
            AddHeaderCell(headerRow, "START", 1, mutedBrush);
            AddHeaderCell(headerRow, "END", 2, mutedBrush);
            AddHeaderCell(headerRow, "%", 3, mutedBrush, TextAlignment.Right);
            container.Children.Add(headerRow);

            foreach (var seg in phases)
            {
                bool isHovered = seg.PhaseName == hoveredSeg?.PhaseName;
                bool isActive  = row.IsActivePhase(seg.PhaseName);
                var phaseColor = PhaseColor(seg.PhaseName);

                var phaseRow = new Grid { Margin = new Thickness(0, 1, 0, 0) };
                phaseRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("92")));
                phaseRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("70")));
                phaseRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("70")));
                phaseRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("32")));

                if (isHovered)
                {
                    phaseRow.Background = new SolidColorBrush(Color.FromArgb(0x22, phaseColor.R, phaseColor.G, phaseColor.B));
                    phaseRow.Margin = new Thickness(-4, 1, -4, 0);
                }

                // Phase dot + name
                var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                namePanel.Children.Add(new Border
                {
                    Width = 7, Height = 7,
                    CornerRadius = new CornerRadius(1.5),
                    Background = new SolidColorBrush(Color.FromArgb(
                        isHovered ? (byte)0xFF : (byte)0xBB, phaseColor.R, phaseColor.G, phaseColor.B)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                namePanel.Children.Add(new TextBlock
                {
                    Text = seg.PhaseName,
                    FontSize = 10.5,
                    FontWeight = isHovered || isActive ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = isHovered || isActive
                        ? new SolidColorBrush(phaseColor)
                        : dimBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(namePanel, 0);
                phaseRow.Children.Add(namePanel);

                AddDataCell(phaseRow, seg.StartDate?.ToString("MMM d") ?? "—", 1, seg.StartDate.HasValue ? contentBrush : mutedBrush);
                AddDataCell(phaseRow, seg.EndDate?.ToString("MMM d") ?? "—", 2, seg.EndDate.HasValue ? contentBrush : mutedBrush);

                string progressStr = seg.Progress > 0 ? $"{(int)Math.Round(seg.Progress * 100)}%" : "—";
                AddDataCell(phaseRow, progressStr, 3, seg.Progress > 0 ? contentBrush : mutedBrush, TextAlignment.Right);

                container.Children.Add(phaseRow);
            }
        }

        // ── Overall span ──────────────────────────────────────────────────────
        if (row.StartDate.HasValue || row.EndDate.HasValue)
        {
            container.Children.Add(new Border { Height = 1, Background = ruleBrush, Margin = new Thickness(0, 3, 0, 1) });

            var spanStart = row.StartDate?.ToString("MMM d, yyyy") ?? "?";
            var spanEnd   = row.EndDate?.ToString("MMM d, yyyy")   ?? "?";
            container.Children.Add(new TextBlock
            {
                Text = $"Span: {spanStart} → {spanEnd}",
                FontSize = 10,
                Foreground = mutedBrush
            });
        }

        return new Border
        {
            Child = container,
            Background = tooltipBg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinWidth = 240,
            MaxWidth = 310
        };
    }

    private static void AddHeaderCell(Grid grid, string text, int column, IBrush brush, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = brush,
            TextAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private static void AddDataCell(Grid grid, string text, int column, IBrush brush, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = brush,
            TextAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    // ── Row reorder preview and hit-testing ──────────────────────────────────

    public void UpdateRowReorderPreview(Point sourcePoint, Point targetPoint)
    {
        if (TryCreateRowReorderIntent(sourcePoint, targetPoint, out var args) && args != null)
        {
            if (!AreSamePreview(_previewReorder, args))
            {
                _previewReorder = args;
                InvalidateVisual();
            }

            return;
        }

        ClearRowReorderPreview();
    }

    public void ClearRowReorderPreview()
    {
        if (_previewReorder == null)
            return;

        _previewReorder = null;
        InvalidateVisual();
    }

    private static bool AreSamePreview(
        GanttRowReorderRequestedEventArgs? left,
        GanttRowReorderRequestedEventArgs? right)
    {
        return left?.SourceRow == right?.SourceRow
            && left?.TargetRow == right?.TargetRow
            && left?.DropBeforeTarget == right?.DropBeforeTarget;
    }

    private void DrawRowReorderPreview(
        DrawingContext ctx,
        IReadOnlyList<GanttRowViewModel> rows,
        double totalWidth)
    {
        if (_previewReorder == null)
            return;

        var targetIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], _previewReorder.TargetRow))
            {
                targetIndex = i;
                break;
            }
        }
        if (targetIndex < 0)
            return;

        var rowTop = HeaderHeight + targetIndex * RowHeight;
        var y = _previewReorder.DropBeforeTarget ? rowTop : rowTop + RowHeight;
        var previewColor = ThemeColor("Cora.Accent.Create", "#F5C842");
        var previewBrush = new SolidColorBrush(previewColor);
        var previewGlow = Tint(previewColor, 0x22);
        var previewPen = new Pen(previewBrush, 2.0);

        ctx.FillRectangle(previewGlow, new Rect(0, rowTop, totalWidth, RowHeight));
        ctx.DrawLine(previewPen, new Point(0, y), new Point(totalWidth, y));
    }

    public bool TryRaiseRowReorderIntent(Point sourcePoint, Point targetPoint)
    {
        if (!TryCreateRowReorderIntent(sourcePoint, targetPoint, out var args) || args == null)
            return false;

        RowReorderRequested?.Invoke(this, args);
        return true;
    }

    public bool TryCreateRowReorderIntent(
        Point sourcePoint,
        Point targetPoint,
        out GanttRowReorderRequestedEventArgs? args)
    {
        args = null;

        var rows = Rows;
        if (rows == null || rows.Count == 0)
            return false;

        if (!TryGetRowHit(sourcePoint, rows, out var sourceIndex, out var sourceRow))
            return false;

        if (!TryGetRowHit(targetPoint, rows, out var targetIndex, out var targetRow))
            return false;

        if (!sourceRow.IsEditable || !targetRow.IsEditable)
            return false;

        if (!RowsSharePartScope(rows, sourceIndex, targetIndex))
            return false;

        var dropBeforeTarget = IsPointBeforeRowMidpoint(targetPoint, targetIndex);
        if (IsNoOpDrop(sourceIndex, targetIndex, dropBeforeTarget))
            return false;

        args = new GanttRowReorderRequestedEventArgs(sourceRow, targetRow, dropBeforeTarget);
        return true;
    }

    private static bool TryGetRowHit(
        Point point,
        IReadOnlyList<GanttRowViewModel> rows,
        out int rowIndex,
        out GanttRowViewModel row)
    {
        rowIndex = -1;
        row = null!;

        if (point.X < 0 || point.Y < HeaderHeight)
            return false;

        rowIndex = (int)((point.Y - HeaderHeight) / RowHeight);
        if (rowIndex < 0 || rowIndex >= rows.Count)
            return false;

        row = rows[rowIndex];
        return true;
    }

    private static bool IsPointBeforeRowMidpoint(Point point, int rowIndex)
    {
        var rowTop = HeaderHeight + rowIndex * RowHeight;
        return point.Y < rowTop + RowHeight / 2.0;
    }

    private static bool IsNoOpDrop(int sourceIndex, int targetIndex, bool dropBeforeTarget)
    {
        if (sourceIndex == targetIndex)
            return true;

        if (dropBeforeTarget && sourceIndex + 1 == targetIndex)
            return true;

        if (!dropBeforeTarget && sourceIndex - 1 == targetIndex)
            return true;

        return false;
    }

    private static bool RowsSharePartScope(
        IReadOnlyList<GanttRowViewModel> rows,
        int sourceIndex,
        int targetIndex)
    {
        return string.Equals(
            GetPartScopeKey(rows, sourceIndex),
            GetPartScopeKey(rows, targetIndex),
            StringComparison.Ordinal);
    }

    private static string GetPartScopeKey(IReadOnlyList<GanttRowViewModel> rows, int rowIndex)
    {
        for (var i = rowIndex; i >= 0; i--)
        {
            var row = rows[i];
            if (row.IsPart)
                return row.RelativePath;

            if (row.IsBook)
                break;
        }

        return string.Empty;
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    private static double DateToX(DateOnly date, DateOnly rangeStart, DateOnly rangeEnd, double chartWidth)
    {
        int totalDays = rangeEnd.DayNumber - rangeStart.DayNumber;
        if (totalDays <= 0) return 0;
        int elapsed = date.DayNumber - rangeStart.DayNumber;
        return Math.Clamp((double)elapsed / totalDays * chartWidth, 0, chartWidth);
    }

    private static GanttEditableColumn GetEditableColumnAtX(double x)
    {
        if (x >= StatusColumnLeft && x < StatusColumnLeft + StatusColumnWidth)
            return GanttEditableColumn.Status;
        if (x >= StartColumnLeft && x < StartColumnLeft + StartColumnWidth)
            return GanttEditableColumn.StartDate;
        if (x >= EndColumnLeft && x < EndColumnLeft + EndColumnWidth)
            return GanttEditableColumn.EndDate;
        if (x >= ProgressColumnLeft && x < ProgressColumnLeft + ProgressColumnWidth)
            return GanttEditableColumn.Progress;
        return GanttEditableColumn.None;
    }

    private static Rect GetCellBounds(int rowIndex, GanttEditableColumn column)
    {
        var rowY = HeaderHeight + rowIndex * RowHeight;
        return column switch
        {
            GanttEditableColumn.Status => new Rect(StatusColumnLeft, rowY, StatusColumnWidth, RowHeight),
            GanttEditableColumn.StartDate => new Rect(StartColumnLeft, rowY, StartColumnWidth, RowHeight),
            GanttEditableColumn.EndDate => new Rect(EndColumnLeft, rowY, EndColumnWidth, RowHeight),
            GanttEditableColumn.Progress => new Rect(ProgressColumnLeft, rowY, ProgressColumnWidth, RowHeight),
            _ => new Rect(0, rowY, 0, RowHeight)
        };
    }

    private static Color StatusToColor(ChapterStatus status)
    {
        var (key, fallback) = GanttRowViewModel.StatusColorKey(status);
        return ThemeColor(key, fallback);
    }

    private static FormattedText MakeFormattedText(string text, double size, IBrush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif"),
            size,
            brush);
    }

    private static void DrawCellText(
        DrawingContext ctx,
        string text,
        double cellX,
        double cellW,
        double rowY,
        double fontSize,
        IBrush brush)
    {
        var ft = MakeFormattedText(TruncateLabel(text, cellW - 12), fontSize, brush);
        ctx.DrawText(ft, new Point(cellX + 6, rowY + (RowHeight - ft.Height) / 2));
    }

    private static string TruncateLabel(string label, double maxWidth)
    {
        int maxChars = Math.Max(1, (int)(maxWidth / 6.5));
        return label.Length <= maxChars ? label : label[..Math.Max(1, maxChars - 3)] + "...";
    }
}
