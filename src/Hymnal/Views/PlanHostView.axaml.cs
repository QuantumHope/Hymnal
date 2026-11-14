using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>Plan mode for story and anthology projects; all behaviour lives in <see cref="ViewModels.PlanHostViewModel"/>.</summary>
public partial class PlanHostView : UserControl
{
    public PlanHostView()
    {
        InitializeComponent();
    }

    // The switch's two buttons set the view rather than toggle it, so clicking the current one leaves it on.
    private void PlannerView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanHostViewModel host)
        {
            host.IsCardsView = false;
            if (sender is ToggleButton button)
                button.IsChecked = true;
        }
    }

    private void CardsView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanHostViewModel host)
        {
            host.IsCardsView = true;
            if (sender is ToggleButton button)
                button.IsChecked = true;
        }
    }
}
