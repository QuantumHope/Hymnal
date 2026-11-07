using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hymnal.Views;

public partial class ConfirmInitializeDialog : Window
{
    public ConfirmInitializeDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> ShowAsync(Window owner) => new ConfirmInitializeDialog().ShowDialog<bool>(owner);

    private void InitializeButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
