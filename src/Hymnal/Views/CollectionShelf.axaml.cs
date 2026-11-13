using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>Plan's collections shelf: open a collection, start one, or add a story that is in none.</summary>
public partial class CollectionShelf : UserControl
{
    public CollectionShelf()
    {
        InitializeComponent();
    }

    /// <summary>Re-reads the collections each time the shelf comes into view (stories may have moved since).</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()
            && DataContext is MainWindowViewModel { CollectionsViewModel: { } collections })
            _ = collections.RefreshAsync();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: CollectionCard { CanOpen: true, Slug: { } slug } }
            && DataContext is MainWindowViewModel vm && Owner is { } owner)
            await CollectionMenu.OpenAsync(owner, vm, slug);
    }

    private async void NewCollection_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && Owner is { } owner)
            await CollectionMenu.NewAsync(owner, vm);
    }

    private async void AddUnassigned_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string path }
            && DataContext is MainWindowViewModel { CollectionsViewModel: { } collections })
        {
            await collections.AddToOpenAsync(path);
            await collections.RefreshAsync();
        }
    }
}
