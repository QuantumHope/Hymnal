namespace Hymnal.Core.Models.Runs;

/// <summary>
/// What a running writing run looks and sounds like (Settings → Runs), stored in the app settings under
/// <see cref="StoreKey"/>. Every layer is on by default; the ambience also needs Settings → Sound turned on.
/// </summary>
public sealed record RunAtmosphereSettings
{
    public const string StoreKey = "run.atmosphere";
    public const double DefaultAmbienceVolume = 0.5;

    /// <summary>Embers and flames rising from the bottom corners.</summary>
    public bool Flames { get; init; } = true;

    /// <summary>A glow around the window edge and a vignette that tightens in the final stretch.</summary>
    public bool EdgeGlow { get; init; } = true;

    /// <summary>Sparks off the word count as it climbs, and combo callouts.</summary>
    public bool Sparks { get; init; } = true;

    /// <summary>The background drone and fire, the final-stretch heartbeat and the finish chime.</summary>
    public bool Ambience { get; init; } = true;

    /// <summary>Ambience volume, 0 to 1.</summary>
    public double AmbienceVolume { get; init; } = DefaultAmbienceVolume;

    /// <summary>A copy with every value in range (settings files can be hand-edited).</summary>
    public RunAtmosphereSettings Normalized() => this with
    {
        AmbienceVolume = double.IsFinite(AmbienceVolume) ? Math.Clamp(AmbienceVolume, 0, 1) : DefaultAmbienceVolume,
    };
}
