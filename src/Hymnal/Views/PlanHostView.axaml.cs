using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>Plan mode; all behaviour lives in <see cref="ViewModels.PlanHostViewModel"/>.</summary>
public partial class PlanHostView : UserControl
{
    public PlanHostView()
    {
        InitializeComponent();
    }

    // The switch's buttons set the view rather than toggle it, so clicking the current one leaves it on.
    private void DetailsView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanHostViewModel host)
            host.ShowDetails();
        KeepChecked(sender);
    }

    private void PlannerView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanHostViewModel host)
            host.ShowBoardTab();
        KeepChecked(sender);
    }

    private void CardsView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanHostViewModel host)
            host.ShowCardsTab();
        KeepChecked(sender);
    }

    private static void KeepChecked(object? sender)
    {
        if (sender is ToggleButton button)
            button.IsChecked = true;
    }
}
