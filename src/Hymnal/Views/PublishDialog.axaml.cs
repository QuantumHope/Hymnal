using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// Shows what LeanPub would get from the open collection (its Book.txt entries in order, missing files) and
/// copies it into manuscript/ on Publish.
/// </summary>
public partial class PublishDialog : Window
{
    private CollectionsViewModel? _collections;

    public PublishDialog()
    {
        InitializeComponent();
    }

    public static async Task ShowAsync(Window owner, CollectionsViewModel collections, string displayName)
    {
        var preview = await collections.PreviewAsync();
        if (preview == null)
            return;
        var dialog = new PublishDialog { _collections = collections };
        dialog.Show(preview, displayName);
        await dialog.ShowDialog(owner);
    }

    private void Show(PublishPreview preview, string displayName)
    {
        var count = preview.Entries.Count;
        HeadingText.Text = $"{displayName}: {count} {(count == 1 ? "entry" : "entries")} in Book.txt";
        EntryList.ItemsSource = preview.Entries;
        ProblemList.ItemsSource = preview.Problems;
        BlockedText.Text = preview.BlockedReason ?? "";
        BlockedText.IsVisible = preview.BlockedReason != null;
        PublishButton.IsEnabled = preview.CanPublish;
    }

    private async void PublishButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_collections == null)
            return;
        PublishButton.IsEnabled = false;
        var result = await _collections.PublishAsync();
        if (result == null)
        {
            PublishButton.IsEnabled = true;
            return;
        }
        ResultText.Text = $"Published: {result.FilesCopied} files copied to manuscript/"
            + (result.FilesRemoved > 0 ? $", {result.FilesRemoved} no longer listed removed." : ".")
            + " Commit and push to send it to LeanPub.";
        ResultText.IsVisible = true;
        PublishButton.Content = "Publish again";
        PublishButton.IsEnabled = true;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
