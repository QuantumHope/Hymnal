using System;
using System.IO;
using ReactiveUI.Builder;
using Xunit;

namespace Hymnal.Views;

public sealed class MainWindowGitPanelSmokeTests
{
    static MainWindowGitPanelSmokeTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void MainWindowAndGitCommitDialogWireGitToolbarAndDialogActions()
    {
        var mainWindowAxaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("GitPanelViewModel.IsVisible", mainWindowAxaml);
        Assert.Contains("GitPanelViewModel.ChangeSummaryText", mainWindowAxaml);
        Assert.Contains("GitPanelViewModel.CanSync", mainWindowAxaml);
        Assert.Contains("GitPanelViewModel.PrimaryActionText", mainWindowAxaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", mainWindowAxaml);
        Assert.Contains("Click=\"SyncButton_Click\"", mainWindowAxaml);
        // The menu/tab bar row is Auto around a 48-high bar, so focus mode can hide it.
        Assert.Contains("RowDefinitions=\"Auto,Auto,*,Auto\"", mainWindowAxaml);
        Assert.Contains("Height=\"48\"", mainWindowAxaml);
        Assert.Contains("BoolNotConverter", mainWindowAxaml);

        var mainWindowCodeBehind = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml.cs"));
        Assert.Contains("ExecuteGitSyncActionAsync", mainWindowCodeBehind);
        Assert.Contains("gitPanel.ChangedFiles", mainWindowCodeBehind);
        Assert.Contains("SyncButton_Click", mainWindowCodeBehind);

        var dialogAxaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/GitCommitDialog.axaml"));
        Assert.Contains("x:Name=\"CommitMessageBox\"", dialogAxaml);
        Assert.Contains("ChangedFileLabels", dialogAxaml);
        Assert.Contains("Content=\"Sync\"", dialogAxaml);
        Assert.Contains("Cancel", dialogAxaml);

        var dialogCodeBehind = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/GitCommitDialog.axaml.cs"));
        Assert.Contains("CommitMessageBox.Text = initialMessage;", dialogCodeBehind);
        Assert.Contains("public string CommitMessage => CommitMessageBox.Text ?? string.Empty;", dialogCodeBehind);
        Assert.Contains("SyncButton_Click", dialogCodeBehind);
        Assert.Contains("CancelButton_Click", dialogCodeBehind);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
