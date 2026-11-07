using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// Story ideas card grid and side editor. Code-behind only opens dialogs and hands their results
/// to <see cref="IdeasViewModel"/>.
/// </summary>
public partial class IdeasView : UserControl
{
    public IdeasView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not IdeasViewModel vm)
                return;

            vm.ConfirmAsync = message => TopLevel.GetTopLevel(this) is Window owner
                ? ConfirmDialog.ShowAsync(owner, "Delete Idea", message, "Delete")
                : Task.FromResult(false);
            vm.AskFileNameAsync = suggestion => TopLevel.GetTopLevel(this) is Window owner
                ? FileNameDialog.ShowAsync(owner, "Promote to Story",
                    "The new story file is created beside the other stories and added to Book.txt.", suggestion)
                : Task.FromResult<string?>(null);
        };
    }

    private async void NewIdeaButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IdeasViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        if (await QuickIdeaDialog.ShowAsync(owner) is { } idea)
            vm.NewIdeaCommand.Execute(idea).Subscribe();
    }
}
