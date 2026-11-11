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
}
