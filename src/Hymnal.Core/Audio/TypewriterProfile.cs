namespace Hymnal.Core.Audio;

/// <summary>
/// The voice of one typewriter: the parameters <see cref="TypewriterSynth"/> renders its sounds from.
/// Frequencies are in Hz, times in milliseconds, gains are linear. Each theme picks a profile through
/// <see cref="ThemeSoundProfiles"/>, so a new theme can sound different by supplying its own.
/// </summary>
public sealed record TypewriterProfile
{
    /// <summary>Stable id; also seeds the rendered variants, so a profile always sounds the same.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>How many different renderings of each sound to keep. More variants, less repetition.</summary>
    public int Variants { get; init; } = 8;

    /// <summary>Scales every random spread between variants: 0 renders identical variants, 1 is the designed spread.</summary>
    public double Variation { get; init; } = 1.0;

    // ── The strike: the type slug hitting the platen ───────────────────────

    /// <summary>Resonant modes of the platen and frame that ring when a slug lands.</summary>
    public IReadOnlyList<double> StrikeModes { get; init; } = [1180, 2420, 3650, 5100];
    public double StrikeQ { get; init; } = 7;
    public double StrikeDecayMs { get; init; } = 15;

    /// <summary>The sharp impact click at the front of every strike.</summary>
    public double ClickHz { get; init; } = 6200;
    public double ClickGain { get; init; } = 0.5;

    /// <summary>Inharmonic modes of the steel typebar, ringing quietly after the strike.</summary>
    public IReadOnlyList<double> RingModes { get; init; } = [2870, 4410, 6730];
    public double RingDecayMs { get; init; } = 38;
    public double RingGain { get; init; } = 0.08;

    /// <summary>The low thump of the rubber platen and the machine's body.</summary>
    public double ThumpHz { get; init; } = 150;
    public double ThumpDecayMs { get; init; } = 18;
    public double ThumpGain { get; init; } = 0.55;

    // ── The escapement: the carriage stepping one space ────────────────────

    public double EscapementDelayMs { get; init; } = 46;
    public double EscapementHz { get; init; } = 3300;
    public double EscapementGain { get; init; } = 0.3;

    // ── Carriage return ────────────────────────────────────────────────────

    /// <summary>How long the carriage takes to slide back.</summary>
    public double ReturnGlideMs { get; init; } = 300;

    /// <summary>Ratchet teeth the carriage passes on the way back.</summary>
    public int ReturnRatchetClicks { get; init; } = 32;

    /// <summary>The margin bell. A gain of 0 leaves it out.</summary>
    public double BellHz { get; init; } = 2130;
    public double BellGain { get; init; } = 0.12;

    // ── Space ──────────────────────────────────────────────────────────────

    /// <summary>A few short early reflections, so the machine sounds like it sits in a room.</summary>
    public double RoomGain { get; init; } = 0.16;

    // ── Output peak level per kind (before the user's volume) ──────────────

    public double KeyLevel { get; init; } = 0.80;
    public double SpaceLevel { get; init; } = 0.70;
    public double ReturnLevel { get; init; } = 0.80;
    public double BackspaceLevel { get; init; } = 0.58;

    public double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => SpaceLevel,
        TypingSoundKind.Return => ReturnLevel,
        TypingSoundKind.Backspace => BackspaceLevel,
        _ => KeyLevel,
    };

    /// <summary>A heavy manual desk typewriter: woody platen thump, crisp slug, a little steel ring.</summary>
    public static TypewriterProfile Manual { get; } = new() { Id = "manual", Name = "Manual typewriter" };
}

/// <summary>
/// Which typewriter each app theme sounds like. Hymnal ships one theme today; when themes are added, map
/// each one here (a theme without an entry gets <see cref="Default"/>).
/// </summary>
public static class ThemeSoundProfiles
{
    public const string SynthwaveThemeId = "synthwave";

    public static TypewriterProfile Default => TypewriterProfile.Manual;

    private static readonly Dictionary<string, TypewriterProfile> ByTheme = new(StringComparer.OrdinalIgnoreCase)
    {
        [SynthwaveThemeId] = TypewriterProfile.Manual,
    };

    public static TypewriterProfile ForTheme(string? themeId) =>
        themeId is not null && ByTheme.TryGetValue(themeId, out var profile) ? profile : Default;
}
