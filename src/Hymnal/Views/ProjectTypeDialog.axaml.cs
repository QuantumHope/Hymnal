using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

public partial class ProjectTypeDialog : Window
{
    private IDisposable? _createSubscription;

    public ProjectTypeDialog()
    {
        InitializeComponent();
    }

    public static async Task<ProjectKind?> ShowAsync(Window owner)
    {
        var dialog = new ProjectTypeDialog();
        var vm = new ProjectTypeDialogViewModel();
        dialog.DataContext = vm;
        dialog._createSubscription = vm.CreateCommand.Subscribe(kind => dialog.Close(kind));
        try
        {
            return await dialog.ShowDialog<ProjectKind?>(owner);
        }
        finally
        {
            dialog._createSubscription?.Dispose();
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
