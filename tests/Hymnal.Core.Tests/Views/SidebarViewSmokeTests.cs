using System;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.Views;
using Hymnal.Views.Converters;
using Xunit;

namespace Hymnal.Core.Tests.Views;

public sealed class SidebarViewSmokeTests
{
    [Fact]
    public void SidebarView_XamlDeclaresRenameAffordanceAndConditionalBookActions()
    {
        var xaml = File.ReadAllText(GetSidebarViewPath());

        Assert.Contains("<ContextMenu Opened=\"ChapterContextMenu_Opened\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Name=\"RenameMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Rename…\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RenameNode_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"{Binding Profile.IncludeInCollectionLabel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding Node.IsExcluded}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"{Binding Profile.ExcludeFromCollectionLabel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Binding Path=\"Node.IsExcluded\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("Converter=\"{StaticResource NodeKindIsChapterAndPresentConverter}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Converter=\"{StaticResource NodeKindAndMissingToForegroundConverter}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"excluded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BoolToFontStyleConverter", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NodeKind.Chapter, false, false, true)]
    [InlineData(NodeKind.Part, false, false, true)]
    [InlineData(NodeKind.Chapter, true, false, false)]
    [InlineData(NodeKind.Part, true, false, false)]
    [InlineData(NodeKind.Chapter, false, true, false)]
    public void CanRenameFromSidebar_MatchesIncludedPresentNodeRules(
        NodeKind kind,
        bool isMissing,
        bool isExcluded,
        bool expected)
    {
        var node = new ChapterNode(
        Key: "node-key",
        RelativePath: "chapters/example.md",
        Title: "Example",
        Kind: kind,
        IsMissing: isMissing,
        Index: 0)
        {
            IsExcluded = isExcluded
        };

        Assert.Equal(expected, SidebarView.CanRenameFromSidebar(node));
    }

    [Theory]
    [InlineData(NodeKind.Chapter, false, false, true)]
    [InlineData(NodeKind.Part, false, false, true)]
    [InlineData(NodeKind.Chapter, true, false, false)]
    [InlineData(NodeKind.Part, true, false, false)]
    [InlineData(NodeKind.Chapter, false, true, false)]
    [InlineData(NodeKind.Part, false, true, false)]
    public void CanDragFromSidebar_AllowsOnlyIncludedPresentChaptersAndParts(
        NodeKind kind,
        bool isMissing,
        bool isExcluded,
        bool expected)
    {
        var node = Node("chapters/example.md", kind, isMissing: isMissing, isExcluded: isExcluded);

        Assert.Equal(expected, SidebarView.CanDragFromSidebar(node));
    }

    [Fact]
    public void CanDragFromSidebar_RejectsBlankPaths()
    {
        var node = Node("", NodeKind.Chapter);

        Assert.False(SidebarView.CanDragFromSidebar(node));
    }

    [Fact]
    public void CanDropOnSidebar_AllowsChapterWithinCurrentPartOnly()
    {
        var partOne = Node("part-one/part.md", NodeKind.Part);
        var chapterOne = Node("part-one/chapter-one.md", NodeKind.Chapter);
        var chapterTwo = Node("part-one/chapter-two.md", NodeKind.Chapter);
        var partTwo = Node("part-two/part.md", NodeKind.Part);
        var chapterThree = Node("part-two/chapter-three.md", NodeKind.Chapter);
        var visibleNodes = new[] { partOne, chapterOne, chapterTwo, partTwo, chapterThree };

        Assert.True(SidebarView.CanDropOnSidebar(chapterOne, chapterTwo, visibleNodes));
        Assert.False(SidebarView.CanDropOnSidebar(chapterOne, chapterThree, visibleNodes));
        Assert.False(SidebarView.CanDropOnSidebar(chapterOne, partOne, visibleNodes));
    }

    [Fact]
    public void CanDropOnSidebar_AllowsPartOnPartButNotChapterTargets()
    {
        var partOne = Node("part-one/part.md", NodeKind.Part);
        var chapterOne = Node("part-one/chapter-one.md", NodeKind.Chapter);
        var partTwo = Node("part-two/part.md", NodeKind.Part);
        var partThree = Node("part-three/part.md", NodeKind.Part);
        var visibleNodes = new[] { partOne, chapterOne, partTwo, partThree };

        Assert.True(SidebarView.CanDropOnSidebar(partOne, partTwo, visibleNodes, dropBefore: true));
        Assert.False(SidebarView.CanDropOnSidebar(partOne, chapterOne, visibleNodes, dropBefore: false));
    }

    [Fact]
    public void CanDropOnSidebar_RejectsPartDropAfterLastPartBecauseViewModelRejectsIt()
    {
        var partOne = Node("part-one/part.md", NodeKind.Part);
        var chapterOne = Node("part-one/chapter-one.md", NodeKind.Chapter);
        var partTwo = Node("part-two/part.md", NodeKind.Part);
        var chapterTwo = Node("part-two/chapter-two.md", NodeKind.Chapter);
        var visibleNodes = new[] { partOne, chapterOne, partTwo, chapterTwo };

        Assert.False(SidebarView.CanDropOnSidebar(partOne, partTwo, visibleNodes, dropBefore: false));
        Assert.True(SidebarView.CanDropOnSidebar(partTwo, partOne, visibleNodes, dropBefore: true));
    }

    [Fact]
    public void CanDropOnSidebar_RejectsSelfDropAndInactiveRows()
    {
        var source = Node("part-one/chapter-one.md", NodeKind.Chapter);
        var excludedTarget = Node("part-one/excluded.md", NodeKind.Chapter, isExcluded: true);
        var missingTarget = Node("part-one/missing.md", NodeKind.Chapter, isMissing: true);

        Assert.False(SidebarView.CanDropOnSidebar(source, source, new[] { source }));
        Assert.False(SidebarView.CanDropOnSidebar(source, excludedTarget, new[] { source, excludedTarget }));
        Assert.False(SidebarView.CanDropOnSidebar(source, missingTarget, new[] { source, missingTarget }));
    }

    [Fact]
    public void NodeKindAndMissingToForegroundConverter_TreatsExcludedChaptersDistinctly()
    {
        var converter = new NodeKindAndMissingToForegroundConverter();

        var included = (SolidColorBrush)converter.Convert(
            new object?[] { NodeKind.Chapter, false, false },
            typeof(IBrush),
            null,
            CultureInfo.InvariantCulture)!;

        var excluded = (SolidColorBrush)converter.Convert(
            new object?[] { NodeKind.Chapter, false, true },
            typeof(IBrush),
            null,
            CultureInfo.InvariantCulture)!;

        var missing = (SolidColorBrush)converter.Convert(
            new object?[] { NodeKind.Chapter, true, false },
            typeof(IBrush),
            null,
            CultureInfo.InvariantCulture)!;

        Assert.NotEqual(included.Color, excluded.Color);
        Assert.NotEqual(excluded.Color, missing.Color);
    }

    private static ChapterNode Node(
        string relativePath,
        NodeKind kind,
        bool isMissing = false,
        bool isExcluded = false) =>
        new(
            Key: string.IsNullOrWhiteSpace(relativePath) ? "node-key" : relativePath,
            RelativePath: relativePath,
            Title: "Example",
            Kind: kind,
            IsMissing: isMissing,
            Index: 0)
        {
            IsExcluded = isExcluded
        };

    private static string GetSidebarViewPath() => Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Hymnal",
            "Views",
            "SidebarView.axaml"));
}
