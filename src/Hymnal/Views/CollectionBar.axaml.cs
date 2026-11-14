using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>The collection bar over Plan and Manage; its title opens <see cref="CollectionMenu"/>.</summary>
public partial class CollectionBar : UserControl
{
    public CollectionBar()
    {
        InitializeComponent();
    }

    private async void CollectionTitleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button
            && DataContext is MainWindowViewModel vm
            && TopLevel.GetTopLevel(this) is Window owner)
            await CollectionMenu.ShowAsync(button, owner, vm);
    }

    private async void CollectionPill_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Hymnal.Core.Models.CollectionInfo { IsActive: false } collection }
            && DataContext is MainWindowViewModel vm
            && TopLevel.GetTopLevel(this) is Window owner)
            await CollectionMenu.OpenAsync(owner, vm, collection.Slug);
    }
}
