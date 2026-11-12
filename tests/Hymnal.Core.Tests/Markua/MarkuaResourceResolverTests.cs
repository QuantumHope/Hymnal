using Hymnal.Core.Markua;

namespace Hymnal.Core.Tests.Markua;

public class MarkuaResourceResolverTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "book"));
    private static readonly string Manuscript = Path.Combine(Root, "manuscript");

    [Fact]
    public void Resolve_PrefersPathNextToChapter()
    {
        var expected = Path.Combine(Manuscript, "map.png");
        var found = MarkuaResourceResolver.Resolve(Manuscript, "map.png", p => p == expected);
        Assert.Equal(expected, found);
    }

    [Fact]
    public void Resolve_FindsLeanpubResourcesFolder()
    {
        var expected = Path.Combine(Manuscript, "resources", "images", "map.png");
        var found = MarkuaResourceResolver.Resolve(Manuscript, "images/map.png", p => p == expected);
        Assert.Equal(expected, found);
    }

    [Fact]
    public void Resolve_WalksUpFromNestedChapterFolder()
    {
        var expected = Path.Combine(Manuscript, "resources", "map.png");
        var found = MarkuaResourceResolver.Resolve(Path.Combine(Manuscript, "part1"), "map.png", p => p == expected);
        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("http://example.com/a.png")]
    [InlineData("")]
    public void Resolve_RemoteOrEmpty_ReturnsNull(string source)
    {
        Assert.Null(MarkuaResourceResolver.Resolve(Manuscript, source, _ => true));
    }

    [Fact]
    public void Resolve_Missing_ReturnsNull()
    {
        Assert.Null(MarkuaResourceResolver.Resolve(Manuscript, "nope.png", _ => false));
        Assert.Null(MarkuaResourceResolver.Resolve(null, "nope.png", _ => true));
    }

    [Fact]
    public void Resolve_UnescapesAndDropsQuery()
    {
        var expected = Path.Combine(Manuscript, "my map.png");
        Assert.Equal(expected, MarkuaResourceResolver.Resolve(Manuscript, "my%20map.png?v=2", p => p == expected));
    }
}
