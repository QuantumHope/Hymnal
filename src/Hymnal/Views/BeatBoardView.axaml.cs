using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// A story's beat board. Code-behind only opens confirmation dialogs and
/// turns drag gestures and focus into <see cref="BeatBoardViewModel"/> calls.
/// </summary>
public partial class BeatBoardView : UserControl
{
    private BeatCardViewModel? _dragSource;
    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStart;
    private bool _dragStarted;

    public BeatBoardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not BeatBoardViewModel vm)
                return;

            vm.ConfirmAsync = request => TopLevel.GetTopLevel(this) is Window owner
                ? ConfirmDialog.ShowAsync(owner, request.Title, request.Message, request.ConfirmText)
                : Task.FromResult(false);
        };
    }

    // ── Selection and drag-to-reorder ─────────────────────────────────────────

    private void BeatCard_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border card || DragDrop.GetAllowDrop(card))
            return;

        DragDrop.SetAllowDrop(card, true);
        DragDrop.AddDragOverHandler(card, BeatCard_DragOver);
        DragDrop.AddDragLeaveHandler(card, BeatCard_DragLeave);
        DragDrop.AddDropHandler(card, BeatCard_Drop);

        card.AddHandler(GotFocusEvent, BeatCard_GotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        card.AddHandler(PointerPressedEvent, BeatCard_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        card.AddHandler(PointerMovedEvent, BeatCard_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        card.AddHandler(PointerReleasedEvent, BeatCard_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Editing a card's fields selects it, so Add inserts after it.</summary>
    private void BeatCard_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Control { DataContext: BeatCardViewModel card } && DataContext is BeatBoardViewModel vm)
            vm.Selected = card;
    }

    private void BeatCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: BeatCardViewModel card } control
            || DataContext is not BeatBoardViewModel { CanEdit: true }
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.FindAncestorOfType<TextBlock>(includeSelf: true) is not { } handle
            || !handle.Classes.Contains("beat-drag-handle"))
            return;

        _dragSource = card;
        _dragPressArgs = e;
        _dragStart = e.GetPosition(this);
        _dragStarted = false;
    }

    private async void BeatCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || _dragStarted || _dragPressArgs is null)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 4 && Math.Abs(point.Y - _dragStart.Y) < 4)
            return;

        _dragStarted = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(_dragSource.Id));
            await DragDrop.DoDragDropAsync(_dragPressArgs, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    private void BeatCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragStarted)
            ClearDragState();
    }

    private void BeatCard_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: BeatCardViewModel target } card
            || _dragSource is null
            || ReferenceEquals(target, _dragSource))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        card.Classes.Add("drop-target");
    }

    private void BeatCard_DragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border card)
            card.Classes.Remove("drop-target");
    }

    private void BeatCard_Drop(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.Classes.Remove("drop-target");

        if (sender is not Control { DataContext: BeatCardViewModel target }
            || _dragSource is not { } source
            || DataContext is not BeatBoardViewModel vm)
            return;

        var from = vm.Beats.IndexOf(source);
        var to = vm.Beats.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;

        e.Handled = true;
        vm.MoveBeatCommand.Execute((from, to)).Subscribe(_ => { }, _ => { /* reported via ThrownExceptions */ });
    }

    private void ClearDragState()
    {
        _dragSource = null;
        _dragPressArgs = null;
        _dragStarted = false;
    }

    // ── The page: read by default, click a field to write it ─────────────────
    // The title and the note show as text; a click swaps that one field for its editor (bound two-way, so the
    // board saves as usual). Clicking away or Enter (title) keeps the edit, Esc puts the text back as it was.

    private TextBox? _editing;
    private Control? _editingDisplay;
    private string _editingOriginal = "";

    private void TitleDisplay_Tapped(object? sender, TappedEventArgs e) => BeginEdit(TitleEditor, TitleDisplay);

    private void NoteDisplay_Tapped(object? sender, TappedEventArgs e) => BeginEdit(NoteEditor, NoteDisplay);

    private void BeginEdit(TextBox editor, Control display, bool selectAll = false)
    {
        if (DataContext is not BeatBoardViewModel { CanEdit: true })
            return;
        EndEdit();
        _editing = editor;
        _editingDisplay = display;
        _editingOriginal = editor.Text ?? "";
        display.IsVisible = false;
        editor.IsVisible = true;
        EditHint.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            if (selectAll)
                editor.SelectAll();
            else
                editor.CaretIndex = editor.Text?.Length ?? 0;
        });
    }

    private void EndEdit(bool revert = false)
    {
        if (_editing is not { } editor)
            return;
        _editing = null;
        if (revert)
            editor.Text = _editingOriginal;
        editor.IsVisible = false;
        if (_editingDisplay != null)
            _editingDisplay.IsVisible = true;
        _editingDisplay = null;
        EditHint.IsVisible = false;
    }

    private void TitleEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EndEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEdit(revert: true);
        }
    }

    private void NoteEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEdit(revert: true);
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            EndEdit();
        }
    }

    private void Editor_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _editing))
            EndEdit();
    }

    /// <summary>A new beat opens on the page with its title selected, ready to type over.</summary>
    private void AddBeatButton_Click(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => BeginEdit(TitleEditor, TitleDisplay, selectAll: true),
            Avalonia.Threading.DispatcherPriority.Background);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _pageSubscription?.Dispose();
        if (DataContext is not BeatBoardViewModel vm)
            return;

        // Turning to another page ends any edit; a board with beats always has a page open (also after another
        // story's board loads in its place).
        void OpenFirstPage()
        {
            if (vm.ShowsBeats && vm.Selected == null && vm.Beats.Count > 0)
                vm.Selected = vm.Beats[0];
        }
        System.Collections.Specialized.NotifyCollectionChangedEventHandler onBeats = (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(OpenFirstPage);
        ((System.Collections.Specialized.INotifyCollectionChanged)vm.Beats).CollectionChanged += onBeats;
        _pageSubscription = new System.Reactive.Disposables.CompositeDisposable(
            ReactiveUI.WhenAnyMixin.WhenAnyValue(vm, x => x.Selected).Subscribe(_ => EndEdit()),
            ReactiveUI.WhenAnyMixin.WhenAnyValue(vm, x => x.ShowsBeats).Subscribe(_ => OpenFirstPage()),
            System.Reactive.Disposables.Disposable.Create(() =>
                ((System.Collections.Specialized.INotifyCollectionChanged)vm.Beats).CollectionChanged -= onBeats));
    }

    private IDisposable? _pageSubscription;
}
