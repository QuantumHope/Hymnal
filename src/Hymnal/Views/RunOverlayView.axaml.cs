using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// The writing run's screens (start card, recap, note to Future You). Only focus and Esc live here: each screen's
/// main control takes focus when it appears (the dial, so arrows and typed minutes work at once), and Esc cancels the start card or closes the recap and note screens.
/// </summary>
public partial class RunOverlayView : UserControl
{
    public RunOverlayView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        StartCard.PropertyChanged += (_, args) => FocusWhenShown(args, Dial);
        RecapCard.PropertyChanged += (_, args) => FocusWhenShown(args, FutureYouButton);
        FutureYouCard.PropertyChanged += (_, args) => FocusWhenShown(args, FutureYouInput);
    }

    private RunViewModel? ViewModel => DataContext as RunViewModel;

    private static void FocusWhenShown(AvaloniaPropertyChangedEventArgs args, InputElement target)
    {
        if (args.Property == IsVisibleProperty && args.GetNewValue<bool>())
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { } vm)
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
