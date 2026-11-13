namespace Hymnal.Core.Audio;

/// <summary>
/// A typing sound scheme: how each kind of keystroke sounds. A scheme composes its sounds from the
/// <see cref="SoundVoice"/> building blocks; <see cref="Render"/> turns one seeded composition into samples.
/// The schemes live in <see cref="SoundSchemes"/>; themes pick theirs through <see cref="ThemeSoundSchemes"/>.
/// </summary>
public abstract class SoundScheme
{
    /// <summary>Stable id, stored in settings; also seeds the rendered variants, so a scheme always sounds the same.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>One line describing the sound, shown in Settings.</summary>
    public abstract string Description { get; }

    /// <summary>How many renderings of each sound to keep. More variants, less repetition.</summary>
    public virtual int Variants => 8;

    /// <summary>Scales every random spread between variants: 0 renders identical variants, 1 is the designed spread.</summary>
    public virtual double Variation => 1.0;

    /// <summary>How much of the room (a few early reflections) the sounds carry.</summary>
    protected virtual double RoomGain => 0.12;

    /// <summary>Peak level of each kind before the user's volume.</summary>
    public abstract double LevelFor(TypingSoundKind kind);

    /// <summary>Writes one rendering of <paramref name="kind"/> into <paramref name="v"/>.</summary>
    protected abstract void Compose(SoundVoice v, TypingSoundKind kind);

    /// <summary>Renders one variant of <paramref name="kind"/> as mono float samples at <see cref="SoundVoice.SampleRate"/>.</summary>
    public float[] Render(TypingSoundKind kind, int seed) => Render(kind, seed, Variation);

    internal float[] Render(TypingSoundKind kind, int seed, double variation)
    {
        var voice = new SoundVoice(seed, variation);
        Compose(voice, kind);
        return voice.Finish(LevelFor(kind), RoomGain);
    }

    public override string ToString() => Name;
}

/// <summary>The sound schemes Hymnal ships.</summary>
public static class SoundSchemes
{
    public static SoundScheme ManualTypewriter { get; } = new ManualTypewriterScheme();
    public static SoundScheme ElectricTypewriter { get; } = new ElectricTypewriterScheme();
    public static SoundScheme MechanicalKeyboard { get; } = new MechanicalKeyboardScheme();
    public static SoundScheme SoftKeys { get; } = new SoftKeysScheme();
    public static SoundScheme Forge { get; } = new ForgeScheme();

    /// <summary>Every scheme, in the order Settings lists them.</summary>
    public static IReadOnlyList<SoundScheme> All { get; } =
        [ManualTypewriter, ElectricTypewriter, MechanicalKeyboard, SoftKeys, Forge];

    /// <summary>The scheme with this id, or null.</summary>
    public static SoundScheme? Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which scheme each app theme sounds like when the typing sound is set to match the theme. Hymnal ships the
/// Synthwave theme today; the other ids are the shared theme catalog (OneRing's themes, CORA's via the planned
/// Eland.Themes package), so themes brought into Hymnal already have a sound. Unlisted themes get <see cref="Default"/>.
/// </summary>
public static class ThemeSoundSchemes
{
    public const string SynthwaveThemeId = "synthwave";

    public static SoundScheme Default => SoundSchemes.ManualTypewriter;

    private static readonly Dictionary<string, SoundScheme> ByTheme = new(StringComparer.OrdinalIgnoreCase)
    {
        // The eighties: an electric typewriter under the neon.
        [SynthwaveThemeId] = SoundSchemes.ElectricTypewriter,

        // Ember, iron and forge-worlds.
        ["mordor-scriptorium"] = SoundSchemes.Forge,
        ["cora-mechanicus"] = SoundSchemes.Forge,

        // Terminals, consoles and ship computers.
        ["cora-muther"] = SoundSchemes.MechanicalKeyboard,
        ["cora-hal"] = SoundSchemes.MechanicalKeyboard,
        ["cora-lcars"] = SoundSchemes.MechanicalKeyboard,
        ["cora-mcp"] = SoundSchemes.MechanicalKeyboard,
        ["cora-skynet"] = SoundSchemes.MechanicalKeyboard,
        ["cora-shodan"] = SoundSchemes.MechanicalKeyboard,
        ["cora-gerty"] = SoundSchemes.MechanicalKeyboard,
        ["cora-daedalus"] = SoundSchemes.MechanicalKeyboard,
        ["cora-icarus"] = SoundSchemes.MechanicalKeyboard,
        ["cora-helios"] = SoundSchemes.MechanicalKeyboard,
        ["cora-cortana"] = SoundSchemes.MechanicalKeyboard,
        ["cora-clippy"] = SoundSchemes.MechanicalKeyboard,

        // Clean, minimal and high-contrast themes: keep it quiet.
        ["cora-light"] = SoundSchemes.SoftKeys,
        ["cora-dark"] = SoundSchemes.SoftKeys,
        ["cora-hc-light"] = SoundSchemes.SoftKeys,
        ["cora-hc-dark"] = SoundSchemes.SoftKeys,
        ["cora-glados"] = SoundSchemes.SoftKeys,
        ["foxen-light"] = SoundSchemes.SoftKeys,
        ["foxen-dark"] = SoundSchemes.SoftKeys,
        ["leading-edje-light"] = SoundSchemes.SoftKeys,
        ["leading-edje-dark"] = SoundSchemes.SoftKeys,

        // Paper, newsrooms and warm rooms: a manual typewriter.
        ["cora-samantha"] = SoundSchemes.ManualTypewriter,
        ["cora-eliza"] = SoundSchemes.ManualTypewriter,
    };

    public static SoundScheme ForTheme(string? themeId) =>
        themeId is not null && ByTheme.TryGetValue(themeId, out var scheme) ? scheme : Default;
}

// ── The schemes ─────────────────────────────────────────────────────────────

/// <summary>
/// A heavy manual desk typewriter: the slug's crack on the platen over a woody body, the escapement stepping the
/// carriage, and a carriage return of lever, ratchet glide and thunk, with a quiet margin bell.
/// </summary>
internal sealed class ManualTypewriterScheme : SoundScheme
{
    public override string Id => "manual-typewriter";
    public override string Name => "Manual typewriter";
    public override string Description => "A heavy desk typewriter: crisp strikes, the escapement's tick, a bell and the carriage sliding home.";
    protected override double RoomGain => 0.14;

    public override double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => 0.72,
        TypingSoundKind.Return => 0.82,
        TypingSoundKind.Backspace => 0.6,
        _ => 0.8,
    };

    protected override void Compose(SoundVoice v, TypingSoundKind kind)
    {
        switch (kind)
        {
            case TypingSoundKind.Space: Space(v); break;
            case TypingSoundKind.Return: Return(v); break;
            case TypingSoundKind.Backspace: Backspace(v); break;
            default: Key(v); break;
        }
    }

    private static void Key(SoundVoice v)
    {
        // The slug striking paper and platen: a crack, the platen's knock and the frame's woody body.
        v.Impact(0, 3400 * v.J(0.1), 0.9, 2.2 * v.J(0.2), 1.0);
        v.Impact(0.2, 1500 * v.J(0.08), 1.4, 5 * v.J(0.2), 0.75);
        v.Impact(0.3, 620 * v.J(0.1), 1.8, 9 * v.J(0.2), 0.55);
        v.Thump(0.2, 115 * v.J(0.08), 12 * v.J(0.15), 0.35);

        // The typebar chattering against its guide.
        v.Impact(3.5 + 2 * v.U(), 2300 * v.J(0.15), 1.2, 1.8, 0.28 * v.J(0.3));
        if (v.Chance(0.5))
            v.Impact(7 + 3 * v.U(), 1900 * v.J(0.15), 1.2, 1.5, 0.15);

        Escapement(v, 38 * v.J(0.15), 1.0);
    }

    private static void Space(SoundVoice v)
    {
        // No slug lands: the wide bar bottoms out with a duller, hollow knock.
        v.Impact(0, 2100 * v.J(0.1), 1.0, 2.5 * v.J(0.2), 0.55);
        v.Impact(0.3, 850 * v.J(0.1), 1.5, 10 * v.J(0.2), 0.7);
        v.Thump(0.2, 95 * v.J(0.08), 14 * v.J(0.15), 0.4);
        v.Impact(5 + 3 * v.U(), 1300 * v.J(0.15), 1.3, 3, 0.2 * v.J(0.3));
        Escapement(v, 30 * v.J(0.15), 1.1);
    }

    private static void Backspace(SoundVoice v)
    {
        // The carriage stepping back one space: a lighter knock and the pawl clicking twice.
        v.Impact(0, 2600 * v.J(0.1), 1.1, 2 * v.J(0.2), 0.5);
        v.Impact(0.3, 1100 * v.J(0.1), 1.4, 6 * v.J(0.2), 0.55);
        v.Thump(0.2, 130 * v.J(0.08), 9, 0.25);
        var first = 14 * v.J(0.2);
        v.Impact(first, 3600 * v.J(0.08), 1.8, 1.0, 0.35 * v.J(0.2));
        v.Impact(first + 14 * v.J(0.25), 3000 * v.J(0.08), 1.8, 1.0, 0.22 * v.J(0.3));
    }

    private static void Return(SoundVoice v)
    {
        v.Bell(0, 2050 * v.J(0.01), 0.07 * v.J(0.15));
        var t = 15.0;

        // Pushing the lever, and the line-feed pawl advancing the platen.
        v.Impact(t, 2400 * v.J(0.1), 1.0, 2.5, 0.6);
        v.Impact(t + 0.3, 900 * v.J(0.1), 1.5, 9, 0.5);
        v.Impact(t + 14 * v.J(0.2), 3300 * v.J(0.08), 1.8, 1.0, 0.35);
        v.Impact(t + 27 * v.J(0.2), 3000 * v.J(0.08), 1.8, 1.0, 0.3);

        // The carriage sliding back over its ratchet.
        var glideStart = t + 50 * v.J(0.2);
        var glide = 280 * v.J(0.15);
        v.Ratchet(glideStart, glide, (int)Math.Round(30 * v.J(0.15)), 3000, 0.18);
        v.Rumble(glideStart, glide, 380 * v.J(0.15), 0.35 * v.J(0.2));

        // And hitting its stop.
        var stop = glideStart + glide + 3 * v.U();
        v.Impact(stop, 2800 * v.J(0.1), 1.0, 3, 0.9);
        v.Impact(stop + 0.3, 1200 * v.J(0.1), 1.4, 8, 0.8);
        v.Impact(stop + 0.5, 480 * v.J(0.1), 1.6, 16, 0.7);
        v.Thump(stop + 0.2, 85 * v.J(0.08), 22, 0.6);
        v.Impact(stop + 6 + 2 * v.U(), 1800 * v.J(0.15), 1.2, 3, 0.25);
    }

    /// <summary>The escapement stepping the carriage one space, then the typebar settling back on its felt.</summary>
    private static void Escapement(SoundVoice v, double atMs, double gain)
    {
        v.Impact(atMs, 4200 * v.J(0.1), 1.8, 1.0, 0.3 * gain * v.J(0.25));
        v.Impact(atMs + 0.4, 1700 * v.J(0.1), 1.4, 2.5, 0.18 * gain);
        v.Impact(atMs + 50 * v.J(0.25), 950 * v.J(0.15), 1.1, 5, 0.12 * v.J(0.3));
    }
}

/// <summary>
/// An office electric typewriter with a type ball: a tight, even "tock" with the mechanism's chunk behind it,
/// and a motorized carriage return instead of a lever.
/// </summary>
internal sealed class ElectricTypewriterScheme : SoundScheme
{
    public override string Id => "electric-typewriter";
    public override string Name => "Electric typewriter";
    public override string Description => "An eighties office electric: a tight, even type-ball tock and a motorized carriage return.";
    protected override double RoomGain => 0.12;

    public override double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => 0.68,
        TypingSoundKind.Return => 0.75,
        TypingSoundKind.Backspace => 0.55,
        _ => 0.75,
    };

    protected override void Compose(SoundVoice v, TypingSoundKind kind)
    {
        switch (kind)
        {
            case TypingSoundKind.Space:
                v.Impact(0, 1500 * v.J(0.08), 1.1, 2.5, 0.5);
                v.Impact(0.3, 520 * v.J(0.08), 1.4, 10 * v.J(0.15), 0.6);
                v.Thump(0.2, 120 * v.J(0.06), 10, 0.35);
                Carrier(v, 20, 1.2);
                break;

            case TypingSoundKind.Backspace:
                v.Impact(0, 2000 * v.J(0.08), 1.1, 2, 0.45);
                v.Impact(0.3, 800 * v.J(0.08), 1.4, 6, 0.45);
                Carrier(v, 16, 1.3);
                break;

            case TypingSoundKind.Return:
                // The motor drives the carrier home, then it clunks against the left margin and indexes a line.
                var run = 160 * v.J(0.1);
                v.Whirr(0, run, 650 * v.J(0.08), 1.2, 0.35);
                v.Rumble(0, run, 300 * v.J(0.1), 0.3);
                var stop = run + 4 * v.U();
                v.Impact(stop, 2200 * v.J(0.08), 1.0, 2.5, 0.8);
                v.Impact(stop + 0.3, 700 * v.J(0.08), 1.4, 10, 0.75);
                v.Thump(stop + 0.2, 100 * v.J(0.06), 16, 0.5);
                v.Impact(stop + 25 * v.J(0.2), 1600 * v.J(0.1), 1.4, 3, 0.3);
                break;

            default:
                // The type ball striking: tight and even, the clutch and mechanism settling behind it.
                v.Impact(0, 2900 * v.J(0.06), 1.0, 1.6 * v.J(0.15), 1.0);
                v.Impact(0.2, 1250 * v.J(0.06), 1.5, 4.5 * v.J(0.15), 0.65);
                v.Impact(0.4, 420 * v.J(0.08), 1.4, 11 * v.J(0.15), 0.45);
                v.Thump(0.2, 150 * v.J(0.05), 8, 0.3);
                v.Impact(9 + 2 * v.U(), 700 * v.J(0.1), 1.2, 6, 0.18 * v.J(0.3));
                Carrier(v, 22, 1.0);
                break;
        }
    }

    /// <summary>The carrier stepping along the platen.</summary>
    private static void Carrier(SoundVoice v, double atMs, double gain) =>
        v.Impact(atMs * v.J(0.15), 2200 * v.J(0.1), 1.5, 1.4, 0.15 * gain * v.J(0.25));
}

/// <summary>
/// A clicky buckling-spring keyboard: the spring's click on the way down, the keycap bottoming out, and the
/// lighter clack as it springs back up. Space and Enter are stabilized, so they knock deeper and rattle.
/// </summary>
internal sealed class MechanicalKeyboardScheme : SoundScheme
{
    public override string Id => "mechanical-keyboard";
    public override string Name => "Mechanical keyboard";
    public override string Description => "A clicky buckling-spring keyboard: click, clack, and deeper stabilized Space and Enter keys.";
    protected override double RoomGain => 0.1;

    public override double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => 0.66,
        TypingSoundKind.Return => 0.72,
        TypingSoundKind.Backspace => 0.64,
        _ => 0.7,
    };

    protected override void Compose(SoundVoice v, TypingSoundKind kind)
    {
        switch (kind)
        {
            case TypingSoundKind.Space: Stabilized(v, 1300, 420, 110, 1.0); break;
            case TypingSoundKind.Return: Stabilized(v, 1700, 600, 130, 0.9); break;
            case TypingSoundKind.Backspace: Key(v, 0.88); break;
            default: Key(v, 1.0); break;
        }
    }

    private static void Key(SoundVoice v, double pitch)
    {
        // The spring buckling: a bright click with the faintest ping.
        v.Impact(0, 5200 * pitch * v.J(0.08), 2.0, 0.9, 0.7 * v.J(0.2));
        v.Ring(0, 3100 * pitch * v.J(0.05), 5 * v.J(0.2), 0.05);

        // The keycap bottoming out on the plate.
        var bottom = 7 * v.J(0.25);
        v.Impact(bottom, 2100 * pitch * v.J(0.08), 1.2, 2.5 * v.J(0.2), 1.0);
        v.Impact(bottom + 0.3, 800 * pitch * v.J(0.1), 1.6, 6 * v.J(0.2), 0.55);
        v.Thump(bottom + 0.2, 180 * v.J(0.08), 7, 0.25);

        // And springing back up.
        var up = bottom + 95 * v.J(0.3);
        v.Impact(up, 2600 * pitch * v.J(0.1), 1.3, 1.8, 0.3 * v.J(0.3));
        v.Impact(up + 0.2, 1000 * pitch * v.J(0.1), 1.5, 4, 0.15);
    }

    private static void Stabilized(SoundVoice v, double hz, double bodyHz, double thumpHz, double gain)
    {
        v.Impact(0, 4800 * v.J(0.08), 2.0, 0.9, 0.4 * v.J(0.2));
        var bottom = 9 * v.J(0.25);
        v.Impact(bottom, hz * v.J(0.08), 1.2, 3.5 * v.J(0.2), 0.8 * gain);
        v.Impact(bottom + 0.3, bodyHz * v.J(0.1), 1.6, 12 * v.J(0.2), 0.75 * gain);
        v.Thump(bottom + 0.2, thumpHz * v.J(0.08), 12, 0.4 * gain);
        // The stabilizer wire rattling in its housings.
        v.Impact(bottom + 4 + 2 * v.U(), 2400 * v.J(0.15), 1.5, 1.5, 0.25 * v.J(0.3));

        var up = bottom + 110 * v.J(0.3);
        v.Impact(up, hz * 1.15 * v.J(0.1), 1.3, 3, 0.35 * v.J(0.3));
        v.Impact(up + 3 + 2 * v.U(), 2200 * v.J(0.15), 1.5, 1.5, 0.15);
    }
}

/// <summary>A quiet laptop keyboard: soft, short membrane taps. Present without drawing attention.</summary>
internal sealed class SoftKeysScheme : SoundScheme
{
    public override string Id => "soft-keys";
    public override string Name => "Soft keys";
    public override string Description => "A quiet laptop keyboard: soft, short taps that stay in the background.";
    protected override double RoomGain => 0.06;

    public override double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => 0.42,
        TypingSoundKind.Return => 0.45,
        TypingSoundKind.Backspace => 0.4,
        _ => 0.45,
    };

    protected override void Compose(SoundVoice v, TypingSoundKind kind)
    {
        switch (kind)
        {
            case TypingSoundKind.Space:
            case TypingSoundKind.Return:
                var hz = kind == TypingSoundKind.Space ? 1000 : 1250;
                v.Impact(0, hz * v.J(0.1), 0.8, 3.5 * v.J(0.2), 1.0);
                v.Impact(0.3, hz * 0.38 * v.J(0.1), 1.2, 8 * v.J(0.2), 0.7);
                v.Impact(3 + v.U(), 1800 * v.J(0.15), 1.0, 1.5, 0.2);
                break;

            default:
                var tap = kind == TypingSoundKind.Backspace ? 1300 : 1500;
                v.Impact(0, tap * v.J(0.1), 0.8, 2.5 * v.J(0.2), 1.0);
                v.Impact(0.3, 600 * v.J(0.1), 1.2, 5 * v.J(0.2), 0.6);
                v.Impact(60 * v.J(0.3), 1200 * v.J(0.15), 1.0, 2, 0.15 * v.J(0.3));
                break;
        }
    }
}

/// <summary>
/// A smith's forge: each key a hammer on the anvil's horn, ringing; Space a blow on the wooden block; Backspace
/// the tongs; and Enter a heavy strike and the hiss of the quench.
/// </summary>
internal sealed class ForgeScheme : SoundScheme
{
    public override string Id => "forge";
    public override string Name => "Forge";
    public override string Description => "A smith at the anvil: ringing hammer blows, the tongs, and the hiss of the quench on Enter.";
    protected override double RoomGain => 0.22;

    public override double LevelFor(TypingSoundKind kind) => kind switch
    {
        TypingSoundKind.Space => 0.62,
        TypingSoundKind.Return => 0.8,
        TypingSoundKind.Backspace => 0.5,
        _ => 0.66,
    };

    /// <summary>An anvil's modes, relative to its fundamental: inharmonic, like any thick block of steel.</summary>
    private static readonly (double Ratio, double DecayMs, double Gain)[] AnvilModes =
        [(1.0, 170, 0.5), (2.31, 120, 0.35), (3.86, 75, 0.25), (5.2, 50, 0.15)];

    protected override void Compose(SoundVoice v, TypingSoundKind kind)
    {
        switch (kind)
        {
            case TypingSoundKind.Space:
                // A blow on the wooden stump, the anvil answering faintly.
                v.Impact(0, 1800 * v.J(0.1), 1.0, 3, 0.7);
                v.Impact(0.3, 520 * v.J(0.1), 1.5, 14 * v.J(0.2), 0.8);
                v.Thump(0.2, 90 * v.J(0.08), 16, 0.5);
                Anvil(v, 0.5, 1050 * v.J(0.04), 0.15);
                break;

            case TypingSoundKind.Backspace:
                // The tongs: two light clinks.
                v.Impact(0, 5500 * v.J(0.1), 1.5, 0.8, 0.4);
                v.Ring(0, 3300 * v.J(0.05), 60 * v.J(0.2), 0.3);
                v.Ring(0, 5100 * v.J(0.05), 40 * v.J(0.2), 0.2);
                var second = 25 * v.J(0.25);
                v.Impact(second, 5200 * v.J(0.1), 1.5, 0.8, 0.25);
                v.Ring(second, 3500 * v.J(0.05), 45, 0.18);
                break;

            case TypingSoundKind.Return:
                // A heavy finishing blow, then the work plunged into the quench.
                v.Impact(0, 4200 * v.J(0.1), 1.0, 1.5, 1.0);
                Anvil(v, 0.3, 840 * v.J(0.04), 1.4);
                v.Thump(0.2, 80 * v.J(0.08), 22, 0.7);
                v.Hiss(45 * v.J(0.2), 25, 380 * v.J(0.2), 4200 * v.J(0.1), 0.35);
                break;

            default:
                // The hammer on the anvil, and its lighter rebound.
                v.Impact(0, 4500 * v.J(0.1), 1.0, 1.2, 0.8);
                var fundamental = 1050 * v.J(0.04);
                Anvil(v, 0.3, fundamental, 1.0);
                v.Thump(0.2, 140 * v.J(0.08), 10, 0.35);
                var rebound = 70 * v.J(0.25);
                v.Impact(rebound, 4000 * v.J(0.1), 1.0, 1.0, 0.25 * v.J(0.3));
                v.Ring(rebound, fundamental, 120, 0.15 * v.J(0.3));
                break;
        }
    }

    private static void Anvil(SoundVoice v, double atMs, double fundamental, double gain)
    {
        foreach (var (ratio, decay, modeGain) in AnvilModes)
            v.Ring(atMs, fundamental * ratio * v.J(0.01), decay * v.J(0.2), modeGain * gain * v.J(0.3));
    }
}
