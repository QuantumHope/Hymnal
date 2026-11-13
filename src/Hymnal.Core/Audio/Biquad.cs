namespace Hymnal.Core.Audio;

/// <summary>
/// A second-order IIR filter (RBJ Audio EQ Cookbook coefficients, transposed direct form II).
/// Mutable: each instance keeps its own state, so use one per signal path.
/// </summary>
internal sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    /// <summary>Band-pass with 0 dB peak gain at <paramref name="frequency"/>.</summary>
    public static Biquad BandPass(double frequency, double q, int sampleRate)
    {
        var (cos, alpha) = Prepare(frequency, q, sampleRate);
        return new Biquad(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad LowPass(double frequency, double q, int sampleRate)
    {
        var (cos, alpha) = Prepare(frequency, q, sampleRate);
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(double frequency, double q, int sampleRate)
    {
        var (cos, alpha) = Prepare(frequency, q, sampleRate);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public double Process(double x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    private static (double Cos, double Alpha) Prepare(double frequency, double q, int sampleRate)
    {
        // Keep the centre safely below Nyquist so a jittered high mode can never make the filter unstable.
        var f = Math.Clamp(frequency, 10, sampleRate * 0.45);
        var w0 = 2 * Math.PI * f / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * Math.Max(q, 0.1)));
    }
}
