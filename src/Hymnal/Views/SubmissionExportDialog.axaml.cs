using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// Exports a story as a Standard Manuscript Format .docx for submission to a market. The work is done by
/// <see cref="SubmissionExportViewModel"/>; this window only opens the saved file or its folder.
/// </summary>
public partial class SubmissionExportDialog : Window
{
    public SubmissionExportDialog()
    {
        InitializeComponent();
    }

    /// <summary>Prepares the export for the story <paramref name="from"/> belongs to and shows the dialog.</summary>
    public static async Task ShowAsync(Window owner, SubmissionExportViewModel export, ChapterViewModel? from = null)
    {
        if (!await export.PrepareAsync(from))
            return;
        var dialog = new SubmissionExportDialog { DataContext = export };
        await dialog.ShowDialog(owner);
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SubmissionExportViewModel export)
            await export.ExportAsync();
    }

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SubmissionExportViewModel { LastExportPath: { } path } && File.Exists(path))
            await Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    private async void ShowFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SubmissionExportViewModel { LastExportPath: { } path }
            && Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
