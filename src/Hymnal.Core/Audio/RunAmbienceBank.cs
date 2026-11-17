namespace Hymnal.Core.Audio;

/// <summary>
/// The sounds of a writing run, synthesised (no audio files): a seamless background loop of a low drone and a
/// crackling fire, a heartbeat for the final stretch, and a chime when the timer runs out. Mono, at
/// <see cref="SoundVoice.SampleRate"/>. Renders the same on every launch; takes a moment, so call it off the UI
/// thread.
/// </summary>
public sealed class RunAmbienceBank
{
    public const double LoopSeconds = 8;

    private RunAmbienceBank(float[] bed, float[] heartbeat, float[] chime)
    {
        Bed = bed;
        Heartbeat = heartbeat;
        Chime = chime;
    }

    public int SampleRate => SoundVoice.SampleRate;

    /// <summary>The background loop: its last sample flows into its first.</summary>
    public float[] Bed { get; }

    /// <summary>One "lub-dub".</summary>
    public float[] Heartbeat { get; }

    /// <summary>The finish chime.</summary>
    public float[] Chime { get; }

    public static RunAmbienceBank Render() => new(RenderBed(), RenderHeartbeat(), RenderChime());

    private static float[] RenderBed()
    {
        const int rate = SoundVoice.SampleRate;
        var length = (int)(LoopSeconds * rate);
        var fade = rate; // one second of overlap, folded back onto the start
        var raw = new double[length + fade];
        var random = new Random(0x5EED);

        // Fire: low-passed noise for the roar, with sparse bright crackles.
        var roar = Biquad.LowPass(380, 0.7, rate);
        var crackleFilter = Biquad.BandPass(2600, 1.4, rate);
        var crackle = 0.0;
        for (var i = 0; i < raw.Length; i++)
        {
            var noise = random.NextDouble() * 2 - 1;
            var sample = roar.Process(noise) * 0.55;

            if (random.NextDouble() < 7.0 / rate) // about seven pops a second
                crackle = 0.4 + random.NextDouble() * 0.6;
            crackle *= 0.9965;
            sample += crackleFilter.Process(noise * crackle) * 0.9;
            raw[i] = sample;
        }

        // Fold the overlap onto the start with an equal-power crossfade, so the loop has no seam.
        var bed = new double[length];
        Array.Copy(raw, bed, length);
        for (var i = 0; i < fade; i++)
        {
            var t = (double)i / fade;
            bed[i] = raw[i] * Math.Sin(t * Math.PI / 2) + raw[length + i] * Math.Cos(t * Math.PI / 2);
        }

        // Drone: whole numbers of cycles in the loop (55, 82.5, 110 Hz over 8 s), breathing every four seconds.
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / rate;
            var breath = 0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.25 * t);
            var drone = Math.Sin(2 * Math.PI * 55 * t) * 0.5
                        + Math.Sin(2 * Math.PI * 82.5 * t) * 0.22
                        + Math.Sin(2 * Math.PI * 110 * t) * 0.12;
            bed[i] += drone * 0.35 * breath;
        }

        return Normalize(bed, 0.55);
    }

    private static float[] RenderHeartbeat()
    {
        const int rate = SoundVoice.SampleRate;
        var samples = new double[(int)(0.55 * rate)];
        Thump(samples, 0.0, 62, 0.11, 1.0);
        Thump(samples, Services.Runs.RunIntensity.DubDelaySeconds, 52, 0.15, 0.7);
        return Normalize(samples, 0.9);

        static void Thump(double[] buffer, double startSeconds, double hz, double decaySeconds, double gain)
        {
            var start = (int)(startSeconds * rate);
            for (var i = start; i < buffer.Length; i++)
            {
                var t = (double)(i - start) / rate;
                var attack = Math.Min(1, t / 0.004);
                // A slight downward pitch bend makes it a thump rather than a tone.
                var phase = 2 * Math.PI * (hz * t + 18 * (1 - Math.Exp(-t / 0.03)) * 0.03);
                buffer[i] += Math.Sin(phase) * attack * Math.Exp(-t / decaySeconds) * gain;
            }
        }
    }

    private static float[] RenderChime()
    {
        const int rate = SoundVoice.SampleRate;
        var samples = new double[(int)(3.2 * rate)];
        // A bell: inharmonic partials, the higher ones dying first.
        (double Ratio, double Gain, double Decay)[] partials =
        [
            (1.0, 1.0, 1.6), (2.0, 0.55, 1.1), (2.76, 0.35, 0.8), (4.07, 0.22, 0.5), (5.43, 0.12, 0.35),
        ];
        const double fundamental = 659.25; // E5
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            var attack = Math.Min(1, t / 0.003);
            var sum = 0.0;
            foreach (var (ratio, gain, decay) in partials)
                sum += Math.Sin(2 * Math.PI * fundamental * ratio * t) * gain * Math.Exp(-t / decay);
            // A fifth below, softer, for warmth.
            sum += Math.Sin(2 * Math.PI * fundamental * 0.5 * t) * 0.4 * Math.Exp(-t / 1.9);
            samples[i] = sum * attack;
        }

        return Normalize(samples, 0.8);
    }

    private static float[] Normalize(double[] samples, double peak)
    {
        var max = 0.0;
        foreach (var s in samples)
            max = Math.Max(max, Math.Abs(s));
        var scale = max > 0 ? peak / max : 0;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            result[i] = (float)(samples[i] * scale);
        return result;
    }
}
