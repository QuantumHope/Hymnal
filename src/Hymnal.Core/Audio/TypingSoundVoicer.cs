namespace Hymnal.Core.Audio;

/// <summary>How to play one keystroke: which variant, and how loud and how high.</summary>
public readonly record struct TypingSoundVoice(int Variant, float Gain, float Pitch);

/// <summary>
/// Turns a stream of keystrokes into varied playback, the way a real typist never strikes two keys alike:
/// it never picks the variant it just played (or the one before, when there are enough), detunes each strike
/// slightly, and varies its level. Quick flurries are played a touch softer and the first key after a pause a
/// touch firmer, which keeps fast typing from turning into a wall of identical clacks.
/// </summary>
public sealed class TypingSoundVoicer
{
    /// <summary>Keystrokes closer together than this count as a flurry.</summary>
    public static readonly TimeSpan FlurryInterval = TimeSpan.FromMilliseconds(75);

    /// <summary>A keystroke after at least this long a pause is struck a little firmer.</summary>
    public static readonly TimeSpan PauseInterval = TimeSpan.FromMilliseconds(1500);

    public const double PitchSpread = 0.025;
    public const double ReturnPitchSpread = 0.008;
    public const double MinGainDb = -1.5;
    public const double MaxGainDb = 1.0;
    public const double FlurryGainDb = -1.5;
    public const double PauseGainDb = 0.8;

    private readonly int _variantCount;
    private readonly Random _rng;
    private readonly Dictionary<TypingSoundKind, List<int>> _recent = new();

    public TypingSoundVoicer(int variantCount, int? seed = null)
    {
        _variantCount = Math.Max(1, variantCount);
        _rng = seed is { } s ? new Random(s) : new Random();
    }

    /// <summary>How many recent picks of a kind are avoided: two when there are enough variants.</summary>
    public int Memory => Math.Min(2, _variantCount - 1);

    public TypingSoundVoice Next(TypingSoundKind kind, TimeSpan sinceLastKeystroke)
    {
        var variant = PickVariant(kind);

        var spread = kind == TypingSoundKind.Return ? ReturnPitchSpread : PitchSpread;
        var pitch = 1 + (_rng.NextDouble() * 2 - 1) * spread;

        var db = MinGainDb + _rng.NextDouble() * (MaxGainDb - MinGainDb);
        if (sinceLastKeystroke < FlurryInterval)
            db += FlurryGainDb;
        else if (sinceLastKeystroke >= PauseInterval)
            db += PauseGainDb;

        return new TypingSoundVoice(variant, (float)Math.Pow(10, db / 20), (float)pitch);
    }

    private int PickVariant(TypingSoundKind kind)
    {
        if (!_recent.TryGetValue(kind, out var recent))
            _recent[kind] = recent = new List<int>(3);

        int pick;
        do
        {
            pick = _rng.Next(_variantCount);
        }
        while (recent.Contains(pick));

        recent.Add(pick);
        while (recent.Count > Memory)
            recent.RemoveAt(0);
        return pick;
    }

    /// <summary>The stereo position for a keystroke at <paramref name="lineFraction"/> of the line (0 = left edge).</summary>
    public static float CarriagePan(double lineFraction, double width = MaxCarriagePan)
    {
        if (!double.IsFinite(lineFraction))
            return 0;
        return (float)((Math.Clamp(lineFraction, 0, 1) * 2 - 1) * width);
    }

    /// <summary>How far left or right the sound can move as the caret travels along the line.</summary>
    public const double MaxCarriagePan = 0.25;
}
