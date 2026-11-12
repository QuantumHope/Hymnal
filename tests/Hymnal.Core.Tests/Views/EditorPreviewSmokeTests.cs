using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Hymnal.Core.Tests.Views;

public sealed class EditorPreviewSmokeTests
{
    [Fact]
    public void EditorView_Axaml_WiresModeSwitchAndPreviewPane()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/EditorView.axaml"));

        Assert.Contains("IsVisible=\"{Binding IsPreviewAvailable}\"", axaml);
        Assert.Contains("CommandParameter=\"{x:Static vm:EditorViewMode.Split}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding ShowSourcePane}\"", axaml);
        Assert.Contains("editor:MarkuaPreviewView", axaml);
        Assert.Contains("Document=\"{Binding PreviewDocument}\"", axaml);
        Assert.Contains("BaseDirectory=\"{Binding PreviewBaseDirectory}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding IsSplitView}\"", axaml);
    }

    [Fact]
    public void F6_CyclesPreviewMode_AndIsBoundOnlyOnce()
    {
        var mainWindow = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("<KeyBinding Gesture=\"F6\" Command=\"{Binding EditorViewModel.CycleViewModeCommand}\" />", mainWindow);
        Assert.Contains("Header=\"Markua _Preview\"", mainWindow);

        var views = Path.GetDirectoryName(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"))!;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
            count += Regex.Matches(File.ReadAllText(file), "<KeyBinding Gesture=\"F6\"").Count;
        Assert.Equal(1, count);
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
