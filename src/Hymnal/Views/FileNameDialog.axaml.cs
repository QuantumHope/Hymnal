using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hymnal.Views;

/// <summary>Asks for a file name, prefilled with a suggestion; returns null when cancelled or left blank.</summary>
public partial class FileNameDialog : Window
{
    public FileNameDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            FileNameInput.Focus();
            FileNameInput.SelectAll();
        };
    }

    public static Task<string?> ShowAsync(Window owner, string title, string prompt, string suggestedName)
    {
        var dialog = new FileNameDialog { Title = title };
        dialog.PromptText.Text = prompt;
        dialog.FileNameInput.Text = suggestedName;
        return dialog.ShowDialog<string?>(owner);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = FileNameInput.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
