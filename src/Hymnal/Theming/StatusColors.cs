using Hymnal.Core.Models;

namespace Hymnal.Theming;

/// <summary>
/// Which theme colour each chapter status uses, everywhere a status is shown (sidebar dots, Gantt bars and legend,
/// corkboard stripes, the status chart). The fallback is Synthwave's value, used when no theme is loaded.
/// </summary>
public static class StatusColors
{
    public static (string Key, string Fallback)? KeyFor(ChapterStatus status) => status switch
    {
        ChapterStatus.Planned   => ("Cora.Accent.Unknown", "#FF6B35"),
        ChapterStatus.Outlining => ("Cora.Text.Secondary", "#9589B0"),
        ChapterStatus.Drafting  => ("Cora.Accent.Retrieve", "#38BDF8"),
        ChapterStatus.Editing   => ("Cora.Accent.Store", "#9D4EDD"),
        ChapterStatus.Polishing => ("Cora.Accent.Create", "#F5C842"),
        ChapterStatus.Reviewing => ("Cora.Accent.Merge", "#E91E8C"),
        ChapterStatus.Done      => ("Cora.Accent.Success", "#22D3A0"),
        _                       => null
    };
}
