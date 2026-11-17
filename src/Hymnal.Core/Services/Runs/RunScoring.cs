namespace Hymnal.Core.Services.Runs;

/// <summary>What a run's edits were mostly about, from the share of words cut.</summary>
public enum ChurnKind
{
    None,
    FreshDrafting,
    DraftingAndShaping,
    DeepRevision,
}

public static class RunScoring
{
    public const int CompletionBonus = 25;
    public const int XpPerMinute = 5;

    /// <summary>
    /// Phase 1 XP: words added, half the words cut (rounded up), five per focused minute, and a bonus for a run
    /// that ran its full time.
    /// </summary>
    public static int Xp(int added, int cut, int actualSeconds, bool completed) =>
        Math.Max(0, added)
        + (Math.Max(0, cut) + 1) / 2
        + XpPerMinute * Math.Max(0, actualSeconds / 60)
        + (completed ? CompletionBonus : 0);

    public static ChurnKind Churn(WordDelta delta)
    {
        if (delta.Touched == 0)
            return ChurnKind.None;
        var cutShare = (double)delta.Cut / delta.Touched;
        return cutShare switch
        {
            < 0.15 => ChurnKind.FreshDrafting,
            <= 0.60 => ChurnKind.DraftingAndShaping,
            _ => ChurnKind.DeepRevision,
        };
    }

    public static string Label(ChurnKind kind) => kind switch
    {
        ChurnKind.FreshDrafting => "Fresh drafting",
        ChurnKind.DraftingAndShaping => "Drafting and shaping",
        ChurnKind.DeepRevision => "Deep revision",
        _ => "No changes",
    };

    /// <summary>Percent-of-target milestones a recap calls out.</summary>
    public static IReadOnlyList<int> Milestones { get; } = [10, 25, 50, 75, 100];

    /// <summary>The highest milestone crossed going from <paramref name="beforePercent"/> to <paramref name="afterPercent"/>, if any.</summary>
    public static int? MilestoneCrossed(double beforePercent, double afterPercent)
    {
        int? crossed = null;
        foreach (var m in Milestones)
        {
            if (beforePercent < m && afterPercent >= m)
                crossed = m;
        }
        return crossed;
    }
}
