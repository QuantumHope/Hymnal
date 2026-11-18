using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// The writing run's screens (the start card, and the recap with its note to Future You). Only focus and keys live
/// here: each screen's main control takes focus when it appears (the dial, so arrows and typed minutes work at
/// once; Done on the recap), Esc cancels the start card or closes the recap (Done, which keeps a typed note), and
/// Ctrl+Enter in the note is Done.
/// </summary>
public partial class RunOverlayView : UserControl
{
    public RunOverlayView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        StartCard.PropertyChanged += (_, args) => FocusWhenShown(args, Dial);
        RecapCard.PropertyChanged += (_, args) => FocusWhenShown(args, DoneButton);
    }

    private RunViewModel? ViewModel => DataContext as RunViewModel;

    private IInputElement? _focusBeforeOpen;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            OnShownChanged(change.GetNewValue<bool>());
    }

    /// <summary>
    /// Remembers what had focus when a screen opens and gives it back when the screen is cancelled or dismissed
    /// (as the command palette does), so typing resumes where it was. Beginning a run is left alone: focus mode
    /// puts the caret in the editor itself.
    /// </summary>
    private void OnShownChanged(bool shown)
    {
        if (shown)
        {
            var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (current is not Visual v || !this.IsVisualAncestorOf(v))
                _focusBeforeOpen = current;
            return;
        }

        var previous = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (ViewModel?.Stage == RunStage.Running)
            return;
        if (previous is Visual { IsEffectivelyVisible: true } visual && TopLevel.GetTopLevel(visual) != null)
            Dispatcher.UIThread.Post(() => previous.Focus(), DispatcherPriority.Input);
    }

    private static void FocusWhenShown(AvaloniaPropertyChangedEventArgs args, InputElement target)
    {
        if (args.Property == IsVisibleProperty && args.GetNewValue<bool>())
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && ((ICommand)vm.DoneCommand).CanExecute(null))
        {
            ((ICommand)vm.DoneCommand).Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape)
            return;

        foreach (ICommand command in new ICommand[] { vm.CancelCommand, vm.DoneCommand })
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
                return;
            }
        }
    }
}
