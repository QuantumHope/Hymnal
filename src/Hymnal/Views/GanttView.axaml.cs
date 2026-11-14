using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hymnal.Core.Models;
using Hymnal.Theming;
using Hymnal.ViewModels;
using ReactiveUI;

namespace Hymnal.Views;

public partial class GanttView : UserControl
{
    private long _lastDatePickerOpenTick;
    private Point? _gridDragStartPoint;
    private GanttRowViewModel? _gridDragSourceRow;
    private bool _gridDragOperationStarted;

    public GanttView()
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(ChapterGrid);
        ArgumentNullException.ThrowIfNull(TimelineCanvas);

        ChapterGrid.AddHandler(PointerPressedEvent, ChapterGrid_PointerPressed, RoutingStrategies.Tunnel);
        ChapterGrid.AddHandler(PointerMovedEvent, ChapterGrid_PointerMoved, RoutingStrategies.Tunnel);
        ChapterGrid.AddHandler(PointerReleasedEvent, ChapterGrid_PointerReleased, RoutingStrategies.Tunnel);
        ChapterGrid.AddHandler(PointerCaptureLostEvent, ChapterGrid_PointerCaptureLost, RoutingStrategies.Tunnel);

        SetupPinnedTimelineScrollBar();

        // A DataGrid column header can't bind to the view-model; it follows the project's noun from here.
        DataContextChanged += (_, _) =>
        {
            _profileSubscription?.Dispose();
            _profileSubscription = null;
            if (DataContext is GanttViewModel vm)
                _profileSubscription = vm.WhenAnyValue(x => x.Profile)
                    .Subscribe(profile =>
                    {
                        if (ChapterGrid.Columns.Count > 0)
                            ChapterGrid.Columns[0].Header = profile.ScheduleItemHeader;
                    });
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _profileSubscription?.Dispose();
            _profileSubscription = null;
        };
    }

    private IDisposable? _profileSubscription;

    // ── Pinned timeline scrollbar ─────────────────────────────────────────────
    // The timeline scrolls inside the rows' vertical ScrollViewer, so its own horizontal bar would sit right
    // under the last row. Instead a ScrollBar in a strip at the bottom of the pane drives it, and the body is
    // kept at least as tall as the viewport so the table's panel runs the full height.

    private bool _syncingTimelineScroll;

    private void SetupPinnedTimelineScrollBar()
    {
        GanttVerticalScroll.GetObservable(ScrollViewer.ViewportProperty)
            .Subscribe(viewport => GanttBody.MinHeight = viewport.Height);

        TimelineHorizontalScroll.ScrollChanged += (_, _) => UpdateTimelineScrollBar();
        TimelineHorizontalScroll.SizeChanged += (_, _) => UpdateTimelineScrollBar();

        TimelineScrollBar.GetObservable(RangeBase.ValueProperty).Subscribe(value =>
        {
            if (_syncingTimelineScroll)
                return;
            var offset = TimelineHorizontalScroll.Offset;
            if (Math.Abs(offset.X - value) > 0.5)
                TimelineHorizontalScroll.Offset = new Vector(value, offset.Y);
        });

        // The strip's left column tracks the table plus its splitter as the splitter is dragged.
        ChapterGrid.SizeChanged += (_, _) => UpdateTimelineScrollStripColumns();
    }

    private void UpdateTimelineScrollStripColumns()
    {
        var left = GanttBody.ColumnDefinitions[0].ActualWidth + GanttBody.ColumnDefinitions[1].ActualWidth;
        if (left > 0)
            TimelineScrollStrip.ColumnDefinitions[0].Width = new GridLength(left);
    }

    private void UpdateTimelineScrollBar()
    {
        var scroll = TimelineHorizontalScroll;
        var max = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        _syncingTimelineScroll = true;
        try
        {
            TimelineScrollBar.Minimum = 0;
            TimelineScrollBar.Maximum = max;
            TimelineScrollBar.ViewportSize = scroll.Viewport.Width;
            TimelineScrollBar.SmallChange = 28;
            TimelineScrollBar.LargeChange = Math.Max(28, scroll.Viewport.Width * 0.9);
            TimelineScrollBar.Value = Math.Min(scroll.Offset.X, max);
        }
        finally
        {
            _syncingTimelineScroll = false;
        }
        TimelineScrollStrip.IsVisible = max > 0.5;
        UpdateTimelineScrollStripColumns();
    }

    private void DatePicker_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is CalendarDatePicker picker && picker.IsEnabled && !picker.IsDropDownOpen)
        {
            var now = Environment.TickCount64;
            if (now - _lastDatePickerOpenTick > 400)
            {
                _lastDatePickerOpenTick = now;
                EnsureClearButton(picker);
                picker.IsDropDownOpen = true;
            }
        }
    }

    private void EnsureClearButton(CalendarDatePicker picker)
    {
        if (picker.Tag is "has-clear")
            return;

        var popup = picker.FindDescendantOfType<Popup>();
        if (popup?.Child == null)
            return;

        var clearBtn = BuildPopupClearButton(picker);

        if (popup.Child is Border border && border.Child is Control calendarContent)
        {
            border.Child = null;
            var panel = new StackPanel();
            panel.Children.Add(calendarContent);
            panel.Children.Add(clearBtn);
            border.Child = panel;
        }
        else
        {
            var existing = popup.Child;
            popup.Child = null;
            var panel = new StackPanel();
            panel.Children.Add(existing);
            panel.Children.Add(clearBtn);
            popup.Child = panel;
        }

        picker.Tag = "has-clear";
    }

    private Button BuildPopupClearButton(CalendarDatePicker picker)
    {
        var btn = new Button
        {
            Content = "Clear date",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = ThemeBrushes.Get("Cora.Surface.Elevated", "#2D1B5E"),
            Foreground = ThemeBrushes.Get("Cora.Text", "#E2DDFF"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = ThemeBrushes.Get("Cora.Border.Input", "#3D2B6E"),
            CornerRadius = new CornerRadius(0, 0, 4, 4),
            Padding = new Thickness(8, 7),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };

        btn.Click += (_, _) =>
        {
            picker.SelectedDate = null;
            picker.IsDropDownOpen = false;
            _lastDatePickerOpenTick = Environment.TickCount64;
        };

        return btn;
    }

    private void ChapterGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsEditingControlFocused() || IsGridDragBlocked(e.Source as AvaloniaObject))
            return;

        if (TryGetGridRowFromEvent(e.Source as AvaloniaObject, out var row) && row.IsEditable)
        {
            _gridDragSourceRow = row;
            _gridDragStartPoint = e.GetPosition(ChapterGrid);
            _gridDragOperationStarted = false;
            ChapterGrid.SelectedItem = row;
            if (DataContext is GanttViewModel vm)
                vm.SelectedRow = row;
            e.Pointer.Capture(ChapterGrid);
            return;
        }

        ResetGridDragState();
    }

    private void ChapterGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gridDragSourceRow == null || _gridDragStartPoint is not { } startPoint)
            return;

        var point = e.GetPosition(ChapterGrid);
        if (!_gridDragOperationStarted)
        {
            if (Math.Abs(point.X - startPoint.X) < 4 && Math.Abs(point.Y - startPoint.Y) < 4)
                return;

            _gridDragOperationStarted = true;
        }

        TimelineCanvas.UpdateRowReorderPreview(ToCanvasRowPoint(startPoint), ToCanvasRowPoint(point));
    }

    private async void ChapterGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gridDragSourceRow == null || _gridDragStartPoint is not { } startPoint)
        {
            ResetGridDragState();
            return;
        }

        try
        {
            await CompleteGridDragForTestsAsync(startPoint, e.GetPosition(ChapterGrid));
        }
        finally
        {
            e.Pointer.Capture(null);
        }
    }

    private void ChapterGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetGridDragState();
    }

    private static bool TryGetGridRowFromEvent(AvaloniaObject? source, out GanttRowViewModel row)
    {
        row = null!;

        if (source is not Visual visual)
            return false;

        var gridRow = visual.FindAncestorOfType<DataGridRow>();
        if (gridRow?.DataContext is not GanttRowViewModel vm)
            return false;

        row = vm;
        return true;
    }

    private static Point ToCanvasRowPoint(Point gridPoint) => new(12, gridPoint.Y);

    private static bool IsGridDragBlocked(AvaloniaObject? source)
    {
        if (source is not Visual visual)
            return false;

        return visual.FindAncestorOfType<CalendarDatePicker>() != null
               || visual.FindAncestorOfType<TextBox>() != null
               || visual.FindAncestorOfType<ComboBox>() != null
               || visual.FindAncestorOfType<Button>() != null;
    }

    private void ResetGridDragState()
    {
        _gridDragStartPoint = null;
        _gridDragSourceRow = null;
        _gridDragOperationStarted = false;
        TimelineCanvas.ClearRowReorderPreview();
    }

    // Reflection hooks for view smoke tests — avoid constructing Avalonia pointer event args.
    private void ChapterGrid_PointerPressedStateForTests(Point point, string relativePath)
    {
        _gridDragSourceRow = DataContext is GanttViewModel vm
            ? vm.Rows.FirstOrDefault(row => string.Equals(row.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            : null;
        _gridDragStartPoint = _gridDragSourceRow != null ? point : null;
        _gridDragOperationStarted = false;
    }

    private void ChapterGrid_PointerMovedStateForTests(Point point)
    {
        if (_gridDragSourceRow == null || _gridDragStartPoint is not { } startPoint)
            return;

        _gridDragOperationStarted = true;
        TimelineCanvas.UpdateRowReorderPreview(ToCanvasRowPoint(startPoint), ToCanvasRowPoint(point));
    }

    private Task ChapterGrid_PointerReleasedStateForTests(Point point)
    {
        if (_gridDragSourceRow == null || _gridDragStartPoint is not { } startPoint)
        {
            ResetGridDragState();
            return Task.CompletedTask;
        }

        return CompleteGridDragForTestsAsync(startPoint, point);
    }

    private async Task CompleteGridDragForTestsAsync(Point startPoint, Point endPoint)
    {
        try
        {
            if (_gridDragOperationStarted
                && TimelineCanvas.TryCreateRowReorderIntent(
                    ToCanvasRowPoint(startPoint),
                    ToCanvasRowPoint(endPoint),
                    out var args)
                && args != null)
            {
                await HandleTimelineRowReorderAsync(args);
            }
        }
        finally
        {
            ResetGridDragState();
        }
    }

    private void ChapterGrid_ContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            throw new InvalidOperationException("GanttView context menu is misconfigured.");

        // Items: 0=View details, 1=Separator, 2=Mark complete
        bool isEditableRow = ChapterGrid.SelectedItem is GanttRowViewModel { IsEditable: true };
        if (menu.Items[0] is MenuItem viewDetails)
            viewDetails.IsVisible = isEditableRow;
        if (menu.Items[2] is MenuItem markComplete)
            markComplete.IsVisible = isEditableRow;
    }

    private async void ViewDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (ChapterGrid.SelectedItem is not GanttRowViewModel row || !row.IsEditable)
            return;

        var detailVm = vm.CreateDetailViewModel(row);
        if (detailVm == null)
            return;

        var dialog = new ChapterDetailDialog { DataContext = detailVm };
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent != null)
            await dialog.ShowDialog(parent);
    }

    private async void MarkComplete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (ChapterGrid.SelectedItem is not GanttRowViewModel row || !row.IsEditable)
            return;

        await vm.MarkRowCompleteAsync(row);
    }

    private async void InlineStatus_DropDownClosed(object? sender, EventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        await vm.SaveInlineCellAsync(row, GanttEditableColumn.Status);
    }

    private async void InlineStart_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        await vm.SaveInlineCellAsync(row, GanttEditableColumn.StartDate);
    }

    private async void InlineEnd_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        await vm.SaveInlineCellAsync(row, GanttEditableColumn.EndDate);
    }

    private async void DateClear_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        var isStart = control.Tag is string tag && tag == "start";
        if (isStart)
        {
            row.EditableStartDate = null;
            await vm.SaveInlineCellAsync(row, GanttEditableColumn.StartDate);
        }
        else
        {
            row.EditableEndDate = null;
            await vm.SaveInlineCellAsync(row, GanttEditableColumn.EndDate);
        }
    }

    private async void InlineProgress_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        await vm.SaveInlineCellAsync(row, GanttEditableColumn.Progress);
    }

    private async void InlineProgress_KeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not GanttRowViewModel row)
            return;

        if (!row.IsEditable)
            return;

        if (e.Key == Key.Enter)
        {
            await vm.SaveInlineCellAsync(row, GanttEditableColumn.Progress);
            e.Handled = true;
        }
    }

    private async void ChapterGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (await TryHandleRowMoveShortcutAsync(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not GanttViewModel vm)
            return;

        if (IsEditingControlFocused())
            return;

        if (ChapterGrid.SelectedItem is not GanttRowViewModel row || !row.IsEditable)
            return;

        var column = GetCurrentColumn();
        if (column == GanttEditableColumn.None)
            return;

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var value = vm.GetCellCopyValue(row, column);
            if (value != null)
            {
                await CopyToClipboardAsync(value);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var clipboardText = await GetClipboardTextAsync();
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                await vm.ApplyClipboardValueAsync(row, column, clipboardText);
                e.Handled = true;
            }
        }
    }

    private async void TimelineCanvas_RowReorderRequested(object? sender, GanttCanvas.GanttRowReorderRequestedEventArgs e)
    {
        await HandleTimelineRowReorderAsync(e);
    }

    private async Task<bool> TryHandleRowMoveShortcutAsync(Key key, KeyModifiers modifiers)
    {
        if (DataContext is not GanttViewModel vm)
            return false;

        if (IsEditingControlFocused())
            return false;

        if (ChapterGrid.SelectedItem is not GanttRowViewModel row || !row.IsEditable)
            return false;

        if (!TryGetRowMoveDirection(key, modifiers, out var moveUp))
            return false;

        vm.SelectedRow = row;
        await ExecuteCommandAsync(moveUp ? vm.MoveSelectedRowUpCommand : vm.MoveSelectedRowDownCommand);
        SyncGridSelection(vm);
        return true;
    }

    private async Task HandleTimelineRowReorderAsync(GanttCanvas.GanttRowReorderRequestedEventArgs e)
    {
        if (DataContext is not GanttViewModel vm)
            return;

        if (e.DropBeforeTarget)
            await vm.MoveRowBeforeAsync(e.SourceRow, e.TargetRow);
        else
            await vm.MoveRowAfterAsync(e.SourceRow, e.TargetRow);

        SyncGridSelection(vm);
    }

    private static bool TryGetRowMoveDirection(Key key, KeyModifiers modifiers, out bool moveUp)
    {
        moveUp = false;

        if (modifiers != KeyModifiers.Alt)
            return false;

        if (key == Key.Up)
        {
            moveUp = true;
            return true;
        }

        if (key == Key.Down)
            return true;

        return false;
    }

    private static async Task ExecuteCommandAsync(System.IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private void SyncGridSelection(GanttViewModel vm)
    {
        if (vm.SelectedRow != null)
            ChapterGrid.SelectedItem = vm.SelectedRow;
    }

    private bool IsEditingControlFocused()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.FocusManager?.GetFocusedElement() is not Visual focused)
            return false;

        return focused.FindAncestorOfType<TextBox>() != null
               || focused.FindAncestorOfType<ComboBox>() != null
               || focused.FindAncestorOfType<CalendarDatePicker>() != null
               || focused.FindAncestorOfType<Calendar>() != null;
    }

    private GanttEditableColumn GetCurrentColumn()
    {
        var index = ChapterGrid.CurrentColumn?.DisplayIndex ?? -1;
        return index switch
        {
            2 => GanttEditableColumn.StartDate,
            3 => GanttEditableColumn.EndDate,
            4 => GanttEditableColumn.Progress,
            _ => GanttEditableColumn.None
        };
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            return await clipboard.TryGetTextAsync();

        return null;
    }
}
