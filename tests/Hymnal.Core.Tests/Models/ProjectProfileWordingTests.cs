using Hymnal.Core.Models;
using Xunit;

namespace Hymnal.Core.Tests.Models;

/// <summary>A short story or anthology never speaks of chapters, parts or books; a novel keeps its words.</summary>
public class ProjectProfileWordingTests
{
    private static ProjectProfile ForKind(ProjectKind kind) => ProjectProfile.For(new ProjectSettings(kind));

    [Theory]
    [InlineData(ProjectKind.Novel, "Chapters", "CHAPTERS", "CHAPTER", "BOOK", "CH", "PART", "BOOK", "new-chapter")]
    [InlineData(ProjectKind.Story, "Scenes", "SCENES", "SCENE", "STORY", "SCENE", "SECTION", "STORY", "new-scene")]
    [InlineData(ProjectKind.Anthology, "Stories", "STORIES", "STORY", "COLLECTION", "SCENE", "STORY", "ALL", "new-story")]
    public void Structure_words_follow_the_kind(ProjectKind kind, string plural, string contents, string scheduleHeader,
        string rollup, string aiUnit, string aiPart, string aiAll, string newFile)
    {
        var p = ForKind(kind);
        Assert.Equal(plural, p.UnitPlural);
        Assert.Equal(contents, p.ContentsTitle);
        Assert.Equal(scheduleHeader, p.ScheduleItemHeader);
        Assert.Equal(rollup, p.ScheduleRollupLabel);
        Assert.Equal(aiUnit, p.AiScopeUnitLabel);
        Assert.Equal(aiPart, p.AiScopePartLabel);
        Assert.Equal(aiAll, p.AiScopeAllLabel);
        Assert.Equal(newFile, p.NewFileStem);
    }

    [Theory]
    [InlineData(ProjectKind.Novel, NodeKind.Chapter, false, "Chapter")]
    [InlineData(ProjectKind.Novel, NodeKind.Chapter, true, "Chapter")]
    [InlineData(ProjectKind.Novel, NodeKind.Part, false, "Part")]
    [InlineData(ProjectKind.Story, NodeKind.Chapter, false, "Scene")]
    [InlineData(ProjectKind.Story, NodeKind.Chapter, true, "Scene")]
    [InlineData(ProjectKind.Story, NodeKind.Part, false, "Section")]
    [InlineData(ProjectKind.Anthology, NodeKind.Chapter, false, "Story")]
    [InlineData(ProjectKind.Anthology, NodeKind.Chapter, true, "Scene")]
    [InlineData(ProjectKind.Anthology, NodeKind.Part, false, "Story")]
    public void Each_entry_is_named_for_what_it_is(ProjectKind kind, NodeKind node, bool insidePart, string expected) =>
        Assert.Equal(expected, ForKind(kind).NounFor(node, insidePart));

    [Theory]
    [InlineData(ProjectKind.Story)]
    [InlineData(ProjectKind.Anthology)]
    public void Short_forms_never_say_chapter_part_or_book(ProjectKind kind)
    {
        var p = ForKind(kind);
        foreach (var text in new[]
                 {
                     p.UnitNoun, p.UnitPlural, p.SubunitNoun, p.SubunitPlural, p.ContentsTitle, p.AddContentsTip,
                     p.NothingOpenTitle, p.NothingOpenHint, p.OpenSomethingTo("take notes"),
                     p.IncludeInCollectionLabel, p.ExcludeFromCollectionLabel, p.InfoPanelTitle,
                     p.AiScopeUnitLabel, p.AiScopePartLabel, p.AiScopeAllLabel,
                     p.ScheduleItemHeader, p.ScheduleRollupLabel, p.NewFileStem, p.NewPartPath
                 })
        {
            Assert.DoesNotContain("chapter", text, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("book", text, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\bpart\b", text.ToLowerInvariant());
        }
    }
}
