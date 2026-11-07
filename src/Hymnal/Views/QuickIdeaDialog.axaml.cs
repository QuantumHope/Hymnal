using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hymnal.Views;

/// <summary>Asks for a new story idea's title (required) and premise (optional).</summary>
public partial class QuickIdeaDialog : Window
{
    public QuickIdeaDialog()
    {
        InitializeComponent();
        CreateButton.IsEnabled = false;
        TitleInput.TextChanged += (_, _) => CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleInput.Text);
        Opened += (_, _) => TitleInput.Focus();
    }

    /// <summary>Shows the dialog; returns the trimmed title and premise, or null when cancelled.</summary>
    public static Task<(string Title, string Premise)?> ShowAsync(Window owner) =>
        new QuickIdeaDialog().ShowDialog<(string Title, string Premise)?>(owner);

    private void CreateButton_Click(object? sender, RoutedEventArgs e)
    {
        var title = TitleInput.Text?.Trim() ?? string.Empty;
        if (title.Length == 0)
            return;

        Close(((string Title, string Premise)?)(title, PremiseInput.Text?.Trim() ?? string.Empty));
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
