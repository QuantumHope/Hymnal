namespace Hymnal.Core.Audio;

/// <summary>
/// One sound being rendered: a buffer, the seeded random source that varies it, and the building blocks a
/// <see cref="SoundScheme"/> composes its sounds from. Times are in milliseconds from the start of the sound,
/// frequencies in Hz, gains linear (the finished sound is normalized, so only their ratios matter).
/// <para>
/// Mechanical sounds are mostly short bursts of broadband noise shaped by low-Q resonances (<see cref="Impact"/>);
/// pure tones (<see cref="Ring"/>) read as metal, so schemes use them only where metal is the point.
/// </para>
/// </summary>
public sealed class SoundVoice
{
    public const int SampleRate = 44100;

    private readonly Random _rng;
    private readonly double _variation;
    private double[] _buf = new double[Samples(1500)];

    internal SoundVoice(int seed, double variation)
    {
        _rng = new Random(seed);
        _variation = variation;
    }

    // ── Variation ───────────────────────────────────────────────────────

    /// <summary>A random factor around 1: 1 ± <paramref name="spread"/>, scaled by the scheme's variation.</summary>
    public double J(double spread) => 1 + (_rng.NextDouble() * 2 - 1) * spread * _variation;

    /// <summary>A random value from 0 to 1, scaled by the scheme's variation (0 when variation is off).</summary>
    public double U() => _rng.NextDouble() * _variation;

    /// <summary>True with probability <paramref name="p"/> (always false when variation is off).</summary>
    public bool Chance(double p) => _variation > 0 && _rng.NextDouble() < p;

    // ── Building blocks ─────────────────────────────────────────────────

    /// <summary>
    /// A struck surface: a burst of noise through a resonance at <paramref name="hz"/>. Low <paramref name="q"/>
    /// (about 1–2) sounds like wood, plastic and rubber; the decay sets how hard or soft the material is.
    /// </summary>
    public void Impact(double atMs, double hz, double q, double decayMs, double gain, double attackMs = 0.08)
    {
        var filter = Biquad.BandPass(hz, q, SampleRate);
        var amp = gain * BandGain(hz, q);
        var start = Samples(atMs);
        var length = Samples(attackMs + decayMs * 7);
        for (var i = 0; i < length; i++)
        {
            var tMs = i * 1000.0 / SampleRate;
            var env = tMs < attackMs ? tMs / attackMs : Math.Exp(-(tMs - attackMs) / decayMs);
            Add(start + i, filter.Process(Noise() * env) * amp);
        }
    }

    /// <summary>A low knock whose pitch drops as it decays, like a rubber platen or a desk taking the blow.</summary>
    public void Thump(double atMs, double hz, double decayMs, double gain)
    {
        var start = Samples(atMs);
        var length = Samples(decayMs * 7);
        var phase = 0.0;
        for (var i = 0; i < length; i++)
        {
            var tMs = i * 1000.0 / SampleRate;
            phase += Math.Tau * hz * (1 + 0.8 * Math.Exp(-tMs / 5)) / SampleRate;
            var env = Math.Min(1, tMs / 0.8) * Math.Exp(-tMs / decayMs);
            Add(start + i, Math.Sin(phase) * env * gain);
        }
    }

    /// <summary>A ringing tone (a damped sine): metal. Used sparingly.</summary>
    public void Ring(double atMs, double hz, double decayMs, double gain, double attackMs = 0.15)
    {
        if (hz >= SampleRate * 0.45)
            return;
        var start = Samples(atMs);
        var length = Samples(decayMs * 7);
        var w = Math.Tau * hz / SampleRate;
        var phase = _rng.NextDouble() * Math.Tau;
        for (var i = 0; i < length; i++)
        {
            var tMs = i * 1000.0 / SampleRate;
            var env = Math.Min(1, tMs / attackMs) * Math.Exp(-tMs / decayMs);
            Add(start + i, Math.Sin(w * i + phase) * env * gain);
        }
    }

    /// <summary>Low, swelling noise: a carriage sliding on its rails.</summary>
    public void Rumble(double atMs, double durationMs, double lowPassHz, double gain)
    {
        var lowPass = Biquad.LowPass(lowPassHz, 0.9, SampleRate);
        var start = Samples(atMs);
        var length = Math.Max(1, Samples(durationMs));
        for (var i = 0; i < length; i++)
        {
            var env = Math.Sin(Math.PI * i / length);
            Add(start + i, lowPass.Process(Noise()) * env * env * gain * 3);
        }
    }

    /// <summary>A band of noise that swells and fades: a motor spinning up and down.</summary>
    public void Whirr(double atMs, double durationMs, double hz, double q, double gain)
    {
        var filter = Biquad.BandPass(hz, q, SampleRate);
        var amp = gain * BandGain(hz, q);
        var start = Samples(atMs);
        var length = Math.Max(1, Samples(durationMs));
        for (var i = 0; i < length; i++)
        {
            var u = i / (double)length;
            var env = Math.Sin(Math.PI * Math.Sqrt(u));
            Add(start + i, filter.Process(Noise()) * env * amp);
        }
    }

    /// <summary>A crackling hiss: hot metal quenched in water.</summary>
    public void Hiss(double atMs, double attackMs, double decayMs, double hz, double gain)
    {
        var highPass = Biquad.HighPass(hz, 0.7, SampleRate);
        var start = Samples(atMs);
        var length = Samples(attackMs + decayMs * 5);
        for (var i = 0; i < length; i++)
        {
            var tMs = i * 1000.0 / SampleRate;
            var env = tMs < attackMs ? tMs / attackMs : Math.Exp(-(tMs - attackMs) / decayMs);
            // Sparse spikes on a quieter bed: the crackle of bubbles bursting.
            var crackle = _rng.NextDouble() < 0.02 ? Noise() * 4 : 0;
            Add(start + i, highPass.Process(Noise() * 0.6 + crackle) * env * gain);
        }
    }

    /// <summary>
    /// A ratchet: <paramref name="teeth"/> ticks over <paramref name="durationMs"/>, spaced like a carriage that
    /// speeds up and then slows down, loudest mid-travel.
    /// </summary>
    public void Ratchet(double atMs, double durationMs, int teeth, double hz, double gain)
    {
        teeth = Math.Max(2, teeth);
        for (var i = 0; i < teeth; i++)
        {
            var u = i / (double)(teeth - 1);
            var at = atMs + durationMs * InverseSmoothStep(u) + 0.5 * (U() - 0.5);
            var swell = 0.55 + 0.45 * Math.Sin(Math.PI * u);
            Impact(at, hz * J(0.12), 1.8, 0.8 * J(0.2), gain * swell * J(0.3));
        }
    }

    /// <summary>A small struck bell (a typewriter's margin bell): inharmonic partials.</summary>
    public void Bell(double atMs, double hz, double gain)
    {
        ReadOnlySpan<(double Ratio, double Gain, double DecayMs)> partials =
            [(1.0, 1.0, 380), (2.0, 0.25, 240), (2.76, 0.35, 170), (5.40, 0.12, 70)];
        foreach (var (ratio, partialGain, decay) in partials)
            Ring(atMs, hz * ratio, decay * J(0.1), gain * partialGain, attackMs: 0.2);
        Impact(atMs, hz * 3, 2, 0.6, gain * 0.3);
    }

    // ── Finishing ───────────────────────────────────────────────────────

    /// <summary>
    /// Adds a few early reflections (<paramref name="roomGain"/>), removes DC, trims the tail below -60 dB, fades out
    /// and normalizes the peak to <paramref name="peak"/>.
    /// </summary>
    internal float[] Finish(double peak, double roomGain)
    {
        var dry = _buf;
        var wet = new double[dry.Length + Samples(40)];
        Array.Copy(dry, wet, dry.Length);

        if (roomGain > 0)
        {
            ReadOnlySpan<(double DelayMs, double Gain)> taps = [(11, 0.6), (17.5, 0.42), (26, 0.25)];
            foreach (var (delayMs, tapGain) in taps)
            {
                var delay = Samples(delayMs * J(0.08));
                var dark = Biquad.LowPass(2400, 0.7, SampleRate);
                var g = roomGain * tapGain;
                for (var i = 0; i < dry.Length && i + delay < wet.Length; i++)
                    wet[i + delay] += dark.Process(dry[i]) * g;
            }
        }

        var dcBlock = Biquad.HighPass(35, 0.7, SampleRate);
        var max = 0.0;
        for (var i = 0; i < wet.Length; i++)
        {
            wet[i] = dcBlock.Process(wet[i]);
            max = Math.Max(max, Math.Abs(wet[i]));
        }

        if (max <= 0)
            return [];

        var end = wet.Length - 1;
        while (end > 0 && Math.Abs(wet[end]) < max * 1e-3)
            end--;
        var length = Math.Min(wet.Length, end + 1 + Samples(5));
        var fade = Math.Min(Samples(8), length);

        var scale = peak / max;
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var fadeOut = i >= length - fade ? (length - i) / (double)fade : 1.0;
            output[i] = (float)(wet[i] * scale * fadeOut);
        }

        return output;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private double Noise() => _rng.NextDouble() * 2 - 1;

    private void Add(int index, double value)
    {
        if (index < 0)
            return;
        if (index >= _buf.Length)
            Array.Resize(ref _buf, Math.Max(index + 1, _buf.Length * 2));
        _buf[index] += value;
    }

    /// <summary>Keeps band-limited noise about as loud as broadband noise: a narrow band passes a sliver of its power.</summary>
    private static double BandGain(double hz, double q) => Math.Sqrt(SampleRate / 2.0 / Math.Max(hz / q, 1));

    /// <summary>Inverse of smoothstep: when the carriage reaches position <paramref name="u"/> (0–1) of its travel.</summary>
    private static double InverseSmoothStep(double u) =>
        0.5 - Math.Sin(Math.Asin(1 - 2 * Math.Clamp(u, 0, 1)) / 3);

    internal static int Samples(double ms) => (int)Math.Round(ms * SampleRate / 1000.0);
}
