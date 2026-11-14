namespace Hymnal.Core.Audio;

/// <summary>Every rendered variant of every typing sound for one scheme.</summary>
public sealed class TypingSoundBank
{
    private readonly Dictionary<TypingSoundKind, IReadOnlyList<float[]>> _variants;

    private TypingSoundBank(SoundScheme scheme, Dictionary<TypingSoundKind, IReadOnlyList<float[]>> variants)
    {
        Scheme = scheme;
        _variants = variants;
    }

    public SoundScheme Scheme { get; }

    public int SampleRate => SoundVoice.SampleRate;

    public IReadOnlyList<float[]> this[TypingSoundKind kind] => _variants[kind];

    public static IReadOnlyList<TypingSoundKind> Kinds { get; } = Enum.GetValues<TypingSoundKind>();

    /// <summary>
    /// Renders <see cref="SoundScheme.Variants"/> variants of each kind. Seeds come from the scheme id, so a scheme
    /// renders identically on every launch. Takes a few tens of milliseconds; call it off the UI thread.
    /// </summary>
    public static TypingSoundBank Render(SoundScheme scheme)
    {
        var baseSeed = StableHash(scheme.Id);
        var count = Math.Max(1, scheme.Variants);
        var variants = new Dictionary<TypingSoundKind, IReadOnlyList<float[]>>();
        foreach (var kind in Kinds)
        {
            var renders = new float[count][];
            for (var i = 0; i < count; i++)
                renders[i] = scheme.Render(kind, unchecked(baseSeed + (int)kind * 7919 + i * 104729));
            variants[kind] = renders;
        }

        return new TypingSoundBank(scheme, variants);
    }

    /// <summary>FNV-1a: unlike string.GetHashCode, the same on every run.</summary>
    internal static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
