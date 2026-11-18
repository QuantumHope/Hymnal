namespace Hymnal.Core.Services.Runs;

/// <summary>
/// How hard a run's atmosphere (flames, glow, ambience) burns at a moment. It idles low for most of the run and
/// climbs through the final stretch: the last minute, or the last quarter of a run shorter than four minutes.
/// </summary>
public static class RunIntensity
{
    /// <summary>The level for most of the run: present but quiet.</summary>
    public const double Idle = 0.15;

    /// <summary>The longest final stretch.</summary>
    public static readonly TimeSpan MaxFinalStretch = TimeSpan.FromSeconds(60);

    /// <summary>The final stretch of a run of <paramref name="planned"/> length.</summary>
    public static TimeSpan FinalStretch(TimeSpan planned) =>
        planned <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(MaxFinalStretch.Ticks, planned.Ticks / 4));

    /// <summary>True while <paramref name="remaining"/> is inside the final stretch (and the run isn't over).</summary>
    public static bool IsFinalStretch(TimeSpan remaining, TimeSpan planned) =>
        remaining > TimeSpan.Zero && remaining <= FinalStretch(planned);

    /// <summary>
    /// The intensity, <see cref="Idle"/> to 1. Inside the final stretch it rises along an ease-in curve, so the
    /// last seconds climb fastest.
    /// </summary>
    public static double At(TimeSpan remaining, TimeSpan planned)
    {
        var stretch = FinalStretch(planned);
        if (stretch <= TimeSpan.Zero || remaining >= stretch)
            return Idle;
        if (remaining <= TimeSpan.Zero)
            return 1;

        var t = 1 - remaining.TotalSeconds / stretch.TotalSeconds; // 0 at the start of the stretch, 1 at the end
        return Idle + (1 - Idle) * t * t;
    }

    /// <summary>The resting heartbeat a run starts at.</summary>
    public const double RestingBpm = 54;

    /// <summary>Where the slow climb through the run has reached when the final stretch begins.</summary>
    public const double StretchStartBpm = 72;

    /// <summary>The racing heartbeat the run ends on.</summary>
    public const double FinalBpm = 144;

    /// <summary>
    /// The run's heartbeat, which beats from the first second: it climbs slowly from <see cref="RestingBpm"/> to
    /// <see cref="StretchStartBpm"/> as <paramref name="progress"/> (0–1 of the run) passes, then races to
    /// <see cref="FinalBpm"/> through the final stretch as <paramref name="intensity"/> rises.
    /// </summary>
    public static double PulseBpm(double progress, double intensity)
    {
        var resting = RestingBpm + (StretchStartBpm - RestingBpm) * Math.Clamp(progress, 0, 1);
        var stretch = Math.Clamp((intensity - Idle) / (1 - Idle), 0, 1);
        return resting + (FinalBpm - resting) * stretch;
    }

    /// <summary>
    /// How loud and bright a beat lands, 0–1 at a moment <paramref name="secondsIntoBeat"/> after it starts: a "lub",
    /// then a softer "dub" <see cref="DubDelaySeconds"/> later, then quiet until the next beat.
    /// </summary>
    public static double BeatEnvelope(double secondsIntoBeat)
    {
        static double Bump(double t, double at, double width) => Math.Exp(-Math.Pow((t - at) / width, 2));
        return Math.Clamp(Bump(secondsIntoBeat, 0.03, 0.045) + 0.65 * Bump(secondsIntoBeat, 0.03 + DubDelaySeconds, 0.055), 0, 1);
    }

    /// <summary>The gap between the two halves of a heartbeat.</summary>
    public const double DubDelaySeconds = 0.16;
}

/// <summary>Net-word combos called out on the HUD as a run's count climbs.</summary>
public static class RunCombos
{
    public static IReadOnlyList<int> Thresholds { get; } = [50, 100, 250, 500, 1000, 2000];

    /// <summary>The highest threshold crossed going from <paramref name="before"/> to <paramref name="after"/> net words.</summary>
    public static int? Crossed(int before, int after)
    {
        int? crossed = null;
        foreach (var t in Thresholds)
        {
            if (before < t && after >= t)
                crossed = t;
        }
        return crossed;
    }

    public static string Callout(int threshold) => threshold switch
    {
        >= 2000 => "LEGENDARY · 2,000 WORDS",
        >= 1000 => "UNSTOPPABLE · 1,000 WORDS",
        >= 500 => "ON FIRE · 500 WORDS",
        >= 250 => "COMBO · 250 WORDS",
        >= 100 => "COMBO · 100 WORDS",
        _ => $"COMBO · {threshold} WORDS",
    };
}
