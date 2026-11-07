using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class GanttProjectionProfileTests
{
    private static readonly ProjectProfile Story = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));
    private static readonly DateOnly Today = new(2026, 11, 7);

    [Fact]
    public void Story_complete_from_drafting_goes_to_editing()
    {
        var r = PhaseCompletion.CompleteAndAdvance(new PhaseData { Status = ChapterStatus.Drafting }, Today, Story);
        Assert.True(r.IsSuccess);
        Assert.Equal(ChapterStatus.Editing, r.Value!.Status);
        Assert.Equal("2026-11-07", r.Value.Schedule!["Drafting"].EndDate);
        Assert.Equal(100.0, r.Value.Schedule["Drafting"].Progress);
    }

    [Fact]
    public void Story_complete_from_polishing_goes_to_done()
    {
        var r = PhaseCompletion.CompleteAndAdvance(new PhaseData { Status = ChapterStatus.Polishing }, Today, Story);
        Assert.True(r.IsSuccess);
        Assert.Equal(ChapterStatus.Done, r.Value!.Status);
        Assert.False(r.Value.Schedule!.ContainsKey("Polishing"));
    }

    [Fact]
    public void Story_complete_from_planned_goes_to_drafting()
    {
        var r = PhaseCompletion.CompleteAndAdvance(new PhaseData { Status = ChapterStatus.Planned }, Today, Story);
        Assert.Equal(ChapterStatus.Drafting, r.Value!.Status);
    }

    [Fact]
    public void Legacy_overload_equals_novel_profile()
    {
        foreach (var status in Enum.GetValues<ChapterStatus>().Where(s => s != ChapterStatus.Done))
        {
            var pd = new PhaseData { Status = status };
            var a = PhaseCompletion.CompleteAndAdvance(pd, Today).Value!;
            var b = PhaseCompletion.CompleteAndAdvance(pd, Today, ProjectProfile.Novel).Value!;
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.PhaseStartDate, b.PhaseStartDate);
            Assert.Equal(a.PhaseEndDate, b.PhaseEndDate);
            Assert.Equal(a.Schedule!.Keys.OrderBy(k => k), b.Schedule!.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void Story_segments_only_drafting_and_editing()
    {
        var segs = GanttProjection.BuildSegments(new PhaseData { Status = ChapterStatus.Drafting }, Story);
        Assert.Equal(["Drafting", "Editing"], segs.Select(s => s.PhaseName));
    }

    [Fact]
    public void Default_phase_data_for_story_is_planned()
    {
        Assert.Equal(ChapterStatus.Planned, PhaseDataService.DefaultPhaseDataFor(Story).Status);
    }
}
