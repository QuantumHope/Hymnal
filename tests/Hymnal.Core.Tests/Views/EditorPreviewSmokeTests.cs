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
    public void ModeButtons_HaveIcons_AndShareOneHeaderRowWithTheChapterBar()
    {
        var editor = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/EditorView.axaml"));
        Assert.Contains("{StaticResource Icon.ViewWrite}", editor);
        Assert.Contains("{StaticResource Icon.ViewSplit}", editor);
        Assert.Contains("{StaticResource Icon.ViewPreview}", editor);
        Assert.Contains("Content=\"{Binding $parent[views:EditorView].HeaderContent}\"", editor);

        var icons = File.ReadAllText(FindRepositoryFile("src/Hymnal/Themes/Icons.axaml"));
        Assert.Contains("x:Key=\"Icon.ViewWrite\"", icons);

        // The chapter bar lives in the editor's header row, not in a bar of its own above the editor.
        var mainWindow = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        var header = mainWindow.IndexOf("<views:EditorView.HeaderContent>", StringComparison.Ordinal);
        var chapterBar = mainWindow.IndexOf("<views:EditorChapterBar", StringComparison.Ordinal);
        Assert.True(header >= 0 && chapterBar > header, "EditorChapterBar should be the Write editor's HeaderContent");
        Assert.Single(Regex.Matches(mainWindow, "<views:EditorChapterBar"));
        Assert.Contains("IsEditorChapterBarVisible}", mainWindow);
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
