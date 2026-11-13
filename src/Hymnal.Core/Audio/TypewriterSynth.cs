namespace Hymnal.Core.Audio;

/// <summary>
/// Renders typewriter sounds from a <see cref="TypewriterProfile"/> by synthesis rather than recordings: each
/// sound is built from an impact click, resonant platen modes excited by filtered noise, a pitched body thump,
/// a quiet steel ring and the escapement tick, then given a few early reflections. Every rendering is seeded,
/// so the same (profile, kind, seed) always gives the same samples, and different seeds give the slightly
/// different strikes that keep a stream of keystrokes from sounding mechanical.
/// </summary>
public sealed class TypewriterSynth
{
    public const int SampleRate = 44100;

    private readonly TypewriterProfile _profile;

    public TypewriterSynth(TypewriterProfile profile)
    {
        _profile = profile;
    }

    /// <summary>Renders one variant of <paramref name="kind"/> as mono float samples at <see cref="SampleRate"/>.</summary>
    public float[] Render(TypingSoundKind kind, int seed)
    {
        var voice = new Voice(_profile, new Random(seed));
        switch (kind)
        {
            case TypingSoundKind.Space: voice.Space(); break;
            case TypingSoundKind.Return: voice.Return(); break;
            case TypingSoundKind.Backspace: voice.Backspace(); break;
            default: voice.Key(); break;
        }

        return voice.Finish(_profile.LevelFor(kind));
    }

    /// <summary>One rendering in progress: a buffer and the random source that varies it.</summary>
    private sealed class Voice
    {
        private readonly TypewriterProfile _p;
        private readonly Random _rng;
        private double[] _buf = new double[Samples(1200)];

        public Voice(TypewriterProfile profile, Random rng)
        {
            _p = profile;
            _rng = rng;
        }

        // ── The four sounds ──────────────────────────────────────────────

        public void Key()
        {
            Click(0, 1.0);
            Strike(0.35, modeScale: 1.0, decayScale: 1.0, gain: 1.0);
            Thump(0.25, 1.0, 1.0, 1.0);
            Ring(0.5, 1.0);

            var escapement = _p.EscapementDelayMs * J(0.15);
            Tick(escapement, _p.EscapementHz, _p.EscapementGain * J(0.25));
            // The key and typebar falling back to rest: a fainter, lower tick.
            Tick(escapement + 45 * J(0.3), _p.EscapementHz * 0.78, _p.EscapementGain * 0.35 * J(0.3));
        }

        public void Space()
        {
            // No slug lands: the wide bar bottoms out with a duller, lower knock, and its ends rattle.
            Click(0, 0.45, hzScale: 0.6);
            Strike(0.4, modeScale: 0.62, decayScale: 1.3, gain: 0.75);
            Strike(7 + 4 * U(), modeScale: 0.7, decayScale: 0.8, gain: 0.22);
            Thump(0.3, 0.75, 1.2, 0.95);
            Tick(_p.EscapementDelayMs * J(0.15), _p.EscapementHz * 0.92, _p.EscapementGain * 1.1 * J(0.25));
        }

        public void Backspace()
        {
            // The carriage stepping back one space: a lighter clunk and the pawl clicking twice.
            Click(0, 0.4, hzScale: 0.8);
            Strike(0.3, modeScale: 0.86, decayScale: 0.7, gain: 0.45);
            Thump(0.2, 1.15, 0.7, 0.5);
            var first = 16 * J(0.2);
            Tick(first, _p.EscapementHz * 1.05, _p.EscapementGain * 1.3 * J(0.2));
            Tick(first + 15 * J(0.25), _p.EscapementHz * 0.9, _p.EscapementGain * 0.7 * J(0.3));
        }

        public void Return()
        {
            var t = 0.0;
            if (_p.BellGain > 0)
            {
                Bell(0, _p.BellGain * J(0.15));
                t = 18;
            }

            // Pushing the lever: the line-feed pawl advances the platen one line.
            Click(t, 0.5, hzScale: 0.85);
            Strike(t + 0.4, modeScale: 0.8, decayScale: 1.0, gain: 0.4);
            Tick(t + 16 * J(0.2), _p.EscapementHz * 0.8, _p.EscapementGain * 1.2);
            Tick(t + 30 * J(0.2), _p.EscapementHz * 0.75, _p.EscapementGain * 0.9);

            // The carriage slides back over its ratchet, speeding up and then slowing down.
            var glideStart = t + 55 * J(0.2);
            var glide = _p.ReturnGlideMs * J(0.15);
            var teeth = Math.Max(4, (int)Math.Round(_p.ReturnRatchetClicks * J(0.15)));
            for (var i = 0; i < teeth; i++)
            {
                var u = i / (double)(teeth - 1);
                var at = glideStart + glide * InverseSmoothStep(u) + 0.6 * (U() - 0.5);
                var swell = 0.55 + 0.45 * Math.Sin(Math.PI * u);
                Tick(at, _p.EscapementHz * 0.9 * J(0.12), _p.EscapementGain * 0.5 * swell * J(0.3), decayMs: 0.6);
            }

            Rumble(glideStart, glide, 0.22 * J(0.2));

            // The carriage hitting its stop.
            var stop = glideStart + glide + 4 * U();
            Click(stop, 0.6, hzScale: 0.7);
            Strike(stop + 0.4, modeScale: 0.55, decayScale: 1.6, gain: 1.0);
            Thump(stop + 0.2, 0.6, 2.0, 1.5);
            Ring(stop + 0.6, 0.6);
        }

        // ── Building blocks ──────────────────────────────────────────────

        /// <summary>The sharp, bright edge of an impact.</summary>
        private void Click(double atMs, double gain, double hzScale = 1.0)
        {
            var hz = _p.ClickHz * hzScale * J(0.1);
            Noise(atMs, attackMs: 0.05, decayMs: 1.1 * J(0.2), _p.ClickGain * gain * J(0.2) * BandGain(hz, 1.2),
                Biquad.BandPass(hz, 1.2, SampleRate));
        }

        /// <summary>The slug landing: each platen mode rings as a damped sine and as a band of noise.</summary>
        private void Strike(double atMs, double modeScale, double decayScale, double gain)
        {
            for (var i = 0; i < _p.StrikeModes.Count; i++)
            {
                var hz = _p.StrikeModes[i] * modeScale * J(0.06);
                var weight = J(0.3) / Math.Sqrt(i + 1);
                var decay = _p.StrikeDecayMs * decayScale * J(0.2) * (1.0 - 0.12 * i);
                Mode(atMs, hz, decay, gain * weight * 0.3, _rng.NextDouble() * Math.Tau);
                Noise(atMs, attackMs: 0.25, decayMs: decay * 0.35, gain * weight * 0.22 * BandGain(hz, _p.StrikeQ),
                    Biquad.BandPass(hz, _p.StrikeQ, SampleRate));
            }
        }

        /// <summary>The typebar's faint metallic ring.</summary>
        private void Ring(double atMs, double gain)
        {
            foreach (var mode in _p.RingModes)
                Mode(atMs, mode * J(0.04), _p.RingDecayMs * J(0.25), _p.RingGain * gain * J(0.4), _rng.NextDouble() * Math.Tau);
        }

        /// <summary>A low knock whose pitch drops as it decays, like a struck rubber platen.</summary>
        private void Thump(double atMs, double hzScale, double decayScale, double gain)
        {
            var hz = _p.ThumpHz * hzScale * J(0.08);
            var decay = _p.ThumpDecayMs * decayScale * J(0.15);
            var amp = _p.ThumpGain * gain * J(0.2);
            var start = Samples(atMs);
            var length = Samples(decay * 7);
            var phase = 0.0;
            for (var i = 0; i < length; i++)
            {
                var tMs = i * 1000.0 / SampleRate;
                var freq = hz * (1 + 0.8 * Math.Exp(-tMs / 6));
                phase += Math.Tau * freq / SampleRate;
                var env = Math.Min(1, tMs / 0.8) * Math.Exp(-tMs / decay);
                Add(start + i, Math.Sin(phase) * env * amp);
            }
        }

        /// <summary>A small mechanical tick: escapement, pawl or ratchet tooth.</summary>
        private void Tick(double atMs, double hz, double gain, double decayMs = 0.9)
        {
            var f = hz * J(0.08);
            Noise(atMs, attackMs: 0.05, decayMs: decayMs * J(0.2), gain * BandGain(f, 2.5), Biquad.BandPass(f, 2.5, SampleRate));
            Mode(atMs, f * 1.37 * J(0.05), 7 * J(0.2), gain * 0.2, _rng.NextDouble() * Math.Tau);
        }

        /// <summary>The margin bell: a struck bell's inharmonic partials.</summary>
        private void Bell(double atMs, double gain)
        {
            var hz = _p.BellHz * J(0.01);
            ReadOnlySpan<(double Ratio, double Gain, double DecayMs)> partials =
                [(1.0, 1.0, 420), (1.99, 0.3, 280), (2.76, 0.45, 200), (5.40, 0.18, 80)];
            foreach (var (ratio, partialGain, decay) in partials)
                Mode(atMs, hz * ratio, decay * J(0.1), gain * partialGain, _rng.NextDouble() * Math.Tau, attackMs: 0.2);
            Noise(atMs, attackMs: 0.05, decayMs: 0.8, gain * 0.4 * BandGain(hz * 3, 2), Biquad.BandPass(hz * 3, 2, SampleRate));
        }

        /// <summary>The low rumble of the carriage sliding on its rails.</summary>
        private void Rumble(double atMs, double durationMs, double gain)
        {
            var lowPass = Biquad.LowPass(420 * J(0.15), 0.9, SampleRate);
            var start = Samples(atMs);
            var length = Samples(durationMs);
            for (var i = 0; i < length; i++)
            {
                var env = Math.Sin(Math.PI * i / length);
                Add(start + i, lowPass.Process(_rng.NextDouble() * 2 - 1) * env * env * gain * 3);
            }
        }

        private void Noise(double atMs, double attackMs, double decayMs, double gain, Biquad filter)
        {
            var start = Samples(atMs);
            var length = Samples(attackMs + decayMs * 7);
            for (var i = 0; i < length; i++)
            {
                var tMs = i * 1000.0 / SampleRate;
                var env = tMs < attackMs ? tMs / attackMs : Math.Exp(-(tMs - attackMs) / decayMs);
                Add(start + i, filter.Process((_rng.NextDouble() * 2 - 1) * env) * gain);
            }
        }

        private void Mode(double atMs, double hz, double decayMs, double gain, double phase, double attackMs = 0.15)
        {
            if (hz >= SampleRate * 0.45)
                return;

            var start = Samples(atMs);
            var length = Samples(decayMs * 7);
            var w = Math.Tau * hz / SampleRate;
            for (var i = 0; i < length; i++)
            {
                var tMs = i * 1000.0 / SampleRate;
                var env = Math.Min(1, tMs / attackMs) * Math.Exp(-tMs / decayMs);
                Add(start + i, Math.Sin(w * i + phase) * env * gain);
            }
        }

        private void Add(int index, double value)
        {
            if (index < 0)
                return;
            if (index >= _buf.Length)
                Array.Resize(ref _buf, Math.Max(index + 1, _buf.Length * 2));
            _buf[index] += value;
        }

        // ── Finishing ───────────────────────────────────────────────────

        /// <summary>Adds the room, removes DC, trims the silent tail, fades out and normalizes to <paramref name="peak"/>.</summary>
        public float[] Finish(double peak)
        {
            var dry = _buf;
            var wet = new double[dry.Length + Samples(40)];
            Array.Copy(dry, wet, dry.Length);

            // Early reflections: three darkened taps off nearby surfaces.
            if (_p.RoomGain > 0)
            {
                ReadOnlySpan<(double DelayMs, double Gain)> taps = [(11, 0.6), (17.5, 0.42), (26, 0.25)];
                foreach (var (delayMs, tapGain) in taps)
                {
                    var delay = Samples(delayMs * J(0.08));
                    var dark = Biquad.LowPass(2600, 0.7, SampleRate);
                    var g = _p.RoomGain * tapGain;
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

            // Trim the tail once it falls below -60 dB of the peak, keeping a few milliseconds for the fade.
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

        // ── Helpers ─────────────────────────────────────────────────────

        /// <summary>A random factor around 1: 1 ± <paramref name="spread"/>, scaled by the profile's variation.</summary>
        private double J(double spread) => 1 + (_rng.NextDouble() * 2 - 1) * spread * _p.Variation;

        private double U() => _rng.NextDouble() * _p.Variation;

        /// <summary>
        /// Gain that keeps band-limited noise roughly as loud as broadband noise: a narrow band passes only a
        /// sliver of white noise's power.
        /// </summary>
        private static double BandGain(double hz, double q) => Math.Sqrt(SampleRate / 2.0 / Math.Max(hz / q, 1));

        /// <summary>Inverse of smoothstep: when the carriage reaches position <paramref name="u"/> (0–1) of its travel.</summary>
        private static double InverseSmoothStep(double u) =>
            0.5 - Math.Sin(Math.Asin(1 - 2 * Math.Clamp(u, 0, 1)) / 3);

        private static int Samples(double ms) => (int)Math.Round(ms * SampleRate / 1000.0);
    }
}
