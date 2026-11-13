using Hymnal.Core.Models;

namespace Hymnal.ViewModels;

/// <summary>How a collection card reads stories out of a collection's list.</summary>
public sealed class CollectionShelfStoryTitlesTests
{
    [Fact]
    public void Files_before_a_divider_are_stories_and_a_divider_is_one_story_with_its_scenes()
    {
        var entries = new[]
        {
            new PublishEntry("lighthouse.md", "Lighthouse", IsPart: false, Exists: true),
            new PublishEntry("harbour.md", "Harbour", IsPart: false, Exists: true),
            new PublishEntry("long/part.md", "The Long Story", IsPart: true, Exists: true),
            new PublishEntry("long/a.md", "Scene A", IsPart: false, Exists: true),
            new PublishEntry("long/b.md", "Scene B", IsPart: false, Exists: true),
            new PublishEntry("other/part.md", "Another", IsPart: true, Exists: true),
        };

        Assert.Equal(["Lighthouse", "Harbour", "The Long Story", "Another"],
            CollectionsViewModel.StoryTitles(entries));
    }
}
