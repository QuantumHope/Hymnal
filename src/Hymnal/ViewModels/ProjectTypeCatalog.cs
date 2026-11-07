using System.Collections.Generic;
using Hymnal.Core.Models;

namespace Hymnal.ViewModels;

public sealed record ProjectTypeInfo(ProjectKind Kind, string Title, string Description, string Impact, string IconKey);

public static class ProjectTypeCatalog
{
    public static IReadOnlyList<ProjectTypeInfo> All { get; } = new[]
    {
        new ProjectTypeInfo(ProjectKind.Novel, "Novel", "One long work in chapters",
            "Full toolset: 7-stage chapter lifecycle, phase scheduling, chapter targets and book-wide stats.",
            "Icon.ProjectNovel"),
        new ProjectTypeInfo(ProjectKind.Story, "Short Story", "One standalone story",
            "Simpler 4-stage status, story-length targets, and a Corkboard of beats from a plot template.",
            "Icon.ProjectStory"),
        new ProjectTypeInfo(ProjectKind.Anthology, "Anthology", "A collection of stories",
            "Short story tools for each story, plus collection order, Parts for long stories and total word count.",
            "Icon.ProjectAnthology"),
    };

    public static string SwitchSummary(ProjectKind target) => target switch
    {
        ProjectKind.Story => "Status labels and Corkboard will switch to short story tools. No data is lost.",
        ProjectKind.Anthology => "Status labels, targets and stats will switch to anthology tools. No data is lost.",
        _ => "Statuses and tools will switch to the full novel toolset. No data is lost.",
    };
}
