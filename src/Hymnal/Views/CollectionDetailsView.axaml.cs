using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// Plan's Details tab. The view model holds the behaviour; here are only the title editor's keys and focus,
/// and the buttons that open the app's dialogs (which need the main window).
/// </summary>
public partial class CollectionDetailsView : UserControl
{
    public CollectionDetailsView()
    {
        InitializeComponent();
        TitleInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && TitleInput.IsVisible)
                Dispatcher.UIThread.Post(() =>
                {
                    TitleInput.Focus();
                    TitleInput.SelectAll();
                }, DispatcherPriority.Loaded);
        };
    }

    private CollectionDetailsViewModel? ViewModel => DataContext as CollectionDetailsViewModel;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private MainWindowViewModel? Main => Owner?.DataContext as MainWindowViewModel;

    private void Title_DoubleTapped(object? sender, TappedEventArgs e) =>
        ViewModel?.BeginRenameCommand.Execute().Subscribe(_ => { }, _ => { });

    private void TitleInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute().Subscribe(_ => { }, _ => { });
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute().Subscribe(_ => { }, _ => { });
            e.Handled = true;
        }
    }

    private void TitleInput_LostFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.CommitRenameCommand.Execute().Subscribe(_ => { }, _ => { });

    private async void Publish_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && Main is { CollectionsViewModel: { } collections } main)
            await PublishDialog.ShowAsync(owner, collections, main.WorkspaceViewModel.CollectionDisplayName);
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && ViewModel?.Export is { } export)
            await SubmissionExportDialog.ShowAsync(owner, export);
    }

    private void BookTxt_Click(object? sender, RoutedEventArgs e) => Main?.OpenBookTxt();

    private async void NewCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && Main is { } main)
            await CollectionMenu.NewAsync(owner, main);
        ViewModel?.Rebuild();
    }
}
