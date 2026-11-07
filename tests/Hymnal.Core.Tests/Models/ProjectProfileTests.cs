using System;
using System.Linq;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Xunit;

namespace Hymnal.Core.Tests.Models;

public class ProjectProfileTests
{
    private static ProjectProfile ForKind(ProjectKind kind) => ProjectProfile.For(new ProjectSettings(kind));

    [Fact]
    public void Novel_profile_matches_legacy_lists()
    {
        var p = ProjectProfile.Novel;
        Assert.Equal(Enum.GetValues<ChapterStatus>(), p.VisibleStatuses.Select(s => s.Value));
        Assert.Equal(Enum.GetValues<ChapterStatus>().Select(s => s.ToString()), p.VisibleStatuses.Select(s => s.Label));
        Assert.Equal(GanttProjection.PhaseNames, p.GanttPhases);
        Assert.Empty(p.TargetPresets);
        Assert.Equal(ChapterStatus.Outlining, p.DefaultStatus);
        foreach (var s in Enum.GetValues<ChapterStatus>())
        {
            Assert.Equal(PhaseCompletion.GetNextStatus(s), p.NextStatus(s));
            Assert.Equal(PhaseCompletion.IsTrackablePhase(s), p.IsTrackablePhase(s));
        }
    }

    [Theory]
    [InlineData(ProjectKind.Story)]
    [InlineData(ProjectKind.Anthology)]
    public void Story_visible_statuses(ProjectKind kind)
    {
        var p = ForKind(kind);
        Assert.Equal(
            new[] { ChapterStatus.Planned, ChapterStatus.Drafting, ChapterStatus.Editing, ChapterStatus.Done },
            p.VisibleStatuses.Select(s => s.Value));
        Assert.Equal(new[] { "Idea", "Drafting", "Revising", "Done" }, p.VisibleStatuses.Select(s => s.Label));
        Assert.Equal(ChapterStatus.Planned, p.DefaultStatus);
    }

    [Fact]
    public void Story_display_maps_legacy_statuses()
    {
        var p = ForKind(ProjectKind.Story);
        Assert.Equal(ChapterStatus.Planned, p.DisplayStatus(ChapterStatus.Outlining));
        Assert.Equal(ChapterStatus.Editing, p.DisplayStatus(ChapterStatus.Polishing));
        Assert.Equal(ChapterStatus.Editing, p.DisplayStatus(ChapterStatus.Reviewing));
        Assert.Equal("Revising", p.LabelFor(ChapterStatus.Polishing));
    }

    [Fact]
    public void Story_next_status_sequence()
    {
        var p = ForKind(ProjectKind.Story);
        Assert.Equal(ChapterStatus.Drafting, p.NextStatus(ChapterStatus.Planned));
        Assert.Equal(ChapterStatus.Editing, p.NextStatus(ChapterStatus.Drafting));
        Assert.Equal(ChapterStatus.Done, p.NextStatus(ChapterStatus.Editing));
        Assert.Equal(ChapterStatus.Done, p.NextStatus(ChapterStatus.Polishing));
        Assert.Equal(ChapterStatus.Drafting, p.NextStatus(ChapterStatus.Outlining));
        Assert.Null(p.NextStatus(ChapterStatus.Done));
    }

    [Fact]
    public void Story_gantt_phases()
    {
        var p = ForKind(ProjectKind.Story);
        Assert.Equal(new[] { "Drafting", "Editing" }, p.GanttPhases);
        Assert.False(p.IsTrackablePhase(ChapterStatus.Polishing));
        Assert.True(p.IsTrackablePhase(ChapterStatus.Drafting));
    }

    [Fact]
    public void Story_target_presets()
    {
        var p = ForKind(ProjectKind.Story);
        Assert.Equal(new[] { "flash", "shortStory", "novelette", "novella", "custom" }, p.TargetPresets.Select(t => t.Key));
        Assert.Equal(new TargetPreset("flash", "Flash", 0, 1000), p.TargetPresets[0]);
        Assert.Equal(new TargetPreset("shortStory", "Short Story", 1000, 7500), p.TargetPresets[1]);
        Assert.Equal(new TargetPreset("novelette", "Novelette", 7500, 17500), p.TargetPresets[2]);
        Assert.Equal(new TargetPreset("novella", "Novella", 17500, 40000), p.TargetPresets[3]);
        Assert.Equal(new TargetPreset("custom", "Custom", null, null), p.TargetPresets[4]);
        Assert.Equal("Novelette", p.PresetLabel("novelette"));
        Assert.Null(p.PresetLabel(null));
        Assert.Null(p.PresetLabel("nope"));
    }

    [Fact]
    public void For_preserves_storage()
    {
        var p = ProjectProfile.For(new ProjectSettings(ProjectKind.Story, StorageMode.Embedded));
        Assert.Equal(StorageMode.Embedded, p.Storage);
        Assert.Equal(ProjectKind.Story, p.Kind);
    }

    [Theory]
    [InlineData(ProjectKind.Novel, "Chapter", "Part", "Book", "Chapter Info", "BOOK STATS", "BY PART", "PART STATS", "BY CHAPTER", true, false)]
    [InlineData(ProjectKind.Story, "Scene", "Part", "Story", "Story Info", "", "", "", "", false, false)]
    [InlineData(ProjectKind.Anthology, "Story", "Story with scenes", "Collection", "Story Info", "COLLECTION STATS", "BY STORY", "STORY STATS", "BY SCENE", true, true)]
    public void Labels_per_kind(ProjectKind kind, string unit, string part, string collection, string info,
        string bookTitle, string bookBar, string partTitle, string partBar, bool showsBook, bool partTargets)
    {
        var p = ForKind(kind);
        Assert.Equal(unit, p.UnitNoun);
        Assert.Equal(part, p.PartNoun);
        Assert.Equal(collection, p.CollectionNoun);
        Assert.Equal(info, p.InfoPanelTitle);
        Assert.Equal(bookTitle, p.BookStatsTitle);
        Assert.Equal(bookBar, p.BookStatsBarLabel);
        Assert.Equal(partTitle, p.PartStatsTitle);
        Assert.Equal(partBar, p.PartStatsBarLabel);
        Assert.Equal(showsBook, p.ShowsBookStats);
        Assert.Equal(partTargets, p.AllowsPartTargets);
    }

    [Theory]
    [InlineData(ProjectKind.Novel, false)]
    [InlineData(ProjectKind.Story, true)]
    [InlineData(ProjectKind.Anthology, true)]
    public void ShowsIdeas_per_kind(ProjectKind kind, bool expected) =>
        Assert.Equal(expected, ForKind(kind).ShowsIdeas);

    [Theory]
    [InlineData(ProjectKind.Novel, false)]
    [InlineData(ProjectKind.Story, true)]
    [InlineData(ProjectKind.Anthology, true)]
    public void UsesBeatBoard_per_kind(ProjectKind kind, bool expected) =>
        Assert.Equal(expected, ForKind(kind).UsesBeatBoard);
}
