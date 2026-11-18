using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hymnal.Views;

/// <summary>The Logbook window: every recorded run. Layout only.</summary>
public partial class RunLogbookDialog : Window
{
    public RunLogbookDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
