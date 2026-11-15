using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// The command palette overlay. Only keyboard routing and focus live here: the arrow keys, Enter and Esc
/// are caught on the way down (before the query box or the window's shortcuts see them), the query box
/// takes focus when the palette opens, and focus goes back where it was when it closes.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private const int PageRows = 8;

    private IInputElement? _focusBeforeOpen;

    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    // Focus follows the overlay's own visibility (bound to IsOpen) rather than IsOpen itself: the box can
    // only take focus once it is visible.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            OnShownChanged(change.GetNewValue<bool>());
    }

    private void OnShownChanged(bool shown)
    {
        if (shown)
        {
            var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (current is not Visual v || !this.IsVisualAncestorOf(v))
                _focusBeforeOpen = current;
            // The box's own effective visibility may only catch up after this change, so retry after layout.
            if (!FocusQuery())
                Dispatcher.UIThread.Post(() => FocusQuery(), DispatcherPriority.Loaded);
        }
        else if (_focusBeforeOpen is { } previous)
        {
            _focusBeforeOpen = null;
            if (previous is Visual visual && TopLevel.GetTopLevel(visual) != null)
                previous.Focus();
        }
    }

    private bool FocusQuery()
    {
        if (!IsVisible || !QueryInput.Focus())
            return false;
        QueryInput.CaretIndex = QueryInput.Text?.Length ?? 0;
        return true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { IsOpen: true } vm)
            return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                break;
            case Key.PageDown:
                vm.MoveSelection(PageRows);
                break;
            case Key.PageUp:
                vm.MoveSelection(-PageRows);
                break;
            case Key.Enter:
                vm.RunSelectedCommand.Execute().Subscribe(_ => { }, _ => { /* reported through ThrownExceptions */ });
                break;
            case Key.Escape:
                vm.Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.Close();
        e.Handled = true;
    }

    private void ResultsList_Tapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && (e.Source as Control)?.DataContext is PaletteEntry entry)
            vm.RunEntryCommand.Execute(entry).Subscribe(_ => { }, _ => { /* reported through ThrownExceptions */ });
    }
}
