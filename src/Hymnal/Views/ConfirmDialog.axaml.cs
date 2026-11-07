using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hymnal.Views;

/// <summary>A yes/no question; Cancel is the default so Enter never confirms by accident.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;
        return dialog.ShowDialog<bool>(owner);
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
