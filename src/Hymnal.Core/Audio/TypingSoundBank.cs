namespace Hymnal.Core.Audio;

/// <summary>Every rendered variant of every typing sound for one profile.</summary>
public sealed class TypingSoundBank
{
    private readonly Dictionary<TypingSoundKind, IReadOnlyList<float[]>> _variants;

    private TypingSoundBank(TypewriterProfile profile, Dictionary<TypingSoundKind, IReadOnlyList<float[]>> variants)
    {
        Profile = profile;
        _variants = variants;
    }

    public TypewriterProfile Profile { get; }

    public int SampleRate => TypewriterSynth.SampleRate;

    public IReadOnlyList<float[]> this[TypingSoundKind kind] => _variants[kind];

    public static IReadOnlyList<TypingSoundKind> Kinds { get; } = Enum.GetValues<TypingSoundKind>();

    /// <summary>
    /// Renders <see cref="TypewriterProfile.Variants"/> variants of each kind. Seeds come from the profile id,
    /// so a profile renders identically on every launch. Takes a few tens of milliseconds; call it off the UI thread.
    /// </summary>
    public static TypingSoundBank Render(TypewriterProfile profile)
    {
        var synth = new TypewriterSynth(profile);
        var baseSeed = StableHash(profile.Id);
        var count = Math.Max(1, profile.Variants);
        var variants = new Dictionary<TypingSoundKind, IReadOnlyList<float[]>>();
        foreach (var kind in Kinds)
        {
            var renders = new float[count][];
            for (var i = 0; i < count; i++)
                renders[i] = synth.Render(kind, unchecked(baseSeed + (int)kind * 7919 + i * 104729));
            variants[kind] = renders;
        }

        return new TypingSoundBank(profile, variants);
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
