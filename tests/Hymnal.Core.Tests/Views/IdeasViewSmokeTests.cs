using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Hymnal.Core.Tests.Views;

public sealed class IdeasViewSmokeTests
{
    [Fact]
    public void MainWindow_Axaml_WiresIdeasNavButtonAndQuickCaptureShortcut()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));

        Assert.Contains("Content=\"IDEAS\"", axaml);
        Assert.Contains("Command=\"{Binding SelectIdeasCommand}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding IsIdeasModeAvailable}\"", axaml);
        Assert.Contains("ConverterParameter=Ideas}", axaml);
        // The shortcut is a command-catalog entry; MainWindow binds catalog gestures.
        var catalog = File.ReadAllText(FindRepositoryFile("src/Hymnal/ViewModels/MainWindowViewModel.Commands.cs"));
        Assert.Contains("() => QuickCaptureIdeaCommand)", catalog);

        // IDEAS sits between RESEARCH and PLAN.
        var research = axaml.IndexOf("Content=\"RESEARCH\"", StringComparison.Ordinal);
        var ideas = axaml.IndexOf("Content=\"IDEAS\"", StringComparison.Ordinal);
        var plan = axaml.IndexOf("Content=\"PLAN\"", StringComparison.Ordinal);
        Assert.True(research < ideas && ideas < plan);
    }

    [Fact]
    public void QuickCaptureShortcut_IsBoundOnlyOnce()
    {
        // Bound once: by the command catalog, never by hand in a view (ShellMenuTests checks that no two catalog
        // commands bind the same gesture).
        var views = Path.GetDirectoryName(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"))!;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
            count += Regex.Matches(File.ReadAllText(file), "<KeyBinding Gesture=\"Ctrl\\+Shift\\+I\"").Count;
        Assert.Equal(0, count);

        var catalog = File.ReadAllText(FindRepositoryFile("src/Hymnal/ViewModels/MainWindowViewModel.Commands.cs"));
        Assert.Equal(1, Regex.Matches(catalog, "Gesture = \"Ctrl\\+Shift\\+I\"").Count);
    }

    [Fact]
    public void IdeasView_Axaml_BindsCardStateOptionsAndEditorCommands()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/IdeasView.axaml"));

        Assert.Contains("x:Class=\"Hymnal.Views.IdeasView\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding Selected.StateOptions}\"", axaml);
        Assert.DoesNotContain("{Binding States}", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedState, Mode=TwoWay}\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding TargetOptions}\"", axaml);
        Assert.Contains("Command=\"{Binding PromoteSelectedCommand}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding ShowsPromote}\"", axaml);
        Assert.Contains("Command=\"{Binding DeleteSelectedCommand}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding ShowUsedAndShelved, Mode=TwoWay}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding TagFilter, Mode=TwoWay}\"", axaml);
        Assert.Contains("MaxLines=\"3\"", axaml);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
