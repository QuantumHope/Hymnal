using Hymnal.Core.Interfaces;

namespace Hymnal.Core.Audio;

/// <summary>
/// App-wide sound preferences, stored in the app settings under <see cref="StoreKey"/>. Room for ambient
/// audio settings to join the typing sounds later.
/// </summary>
public sealed record SoundSettings
{
    public const string StoreKey = "sound";
    public const double DefaultTypingVolume = 0.6;

    /// <summary><see cref="TypingSound"/> value: use the current theme's scheme (<see cref="ThemeSoundSchemes"/>).</summary>
    public const string MatchTheme = "theme";

    /// <summary><see cref="TypingSound"/> value: no typing sound.</summary>
    public const string Silent = "silent";

    /// <summary>Whether Hymnal makes any sound. Off until the user turns it on.</summary>
    public bool SoundEnabled { get; init; }

    /// <summary>
    /// The typing sound: <see cref="MatchTheme"/>, <see cref="Silent"/>, or a <see cref="SoundScheme.Id"/>.
    /// An unknown id (a scheme since removed, a hand edit) is treated as <see cref="MatchTheme"/>.
    /// </summary>
    public string TypingSound { get; init; } = MatchTheme;

    /// <summary>Typing sound volume, 0 to 1.</summary>
    public double TypingVolume { get; init; } = DefaultTypingVolume;

    /// <summary>A copy with every value in range (settings files can be hand-edited).</summary>
    public SoundSettings Normalized() => this with
    {
        TypingVolume = double.IsFinite(TypingVolume) ? Math.Clamp(TypingVolume, 0, 1) : DefaultTypingVolume,
        TypingSound = NormalizeTypingSound(TypingSound),
    };

    /// <summary><paramref name="choice"/> as a known value: Silent, a scheme id, or otherwise MatchTheme.</summary>
    public static string NormalizeTypingSound(string? choice)
    {
        if (string.Equals(choice, Silent, StringComparison.OrdinalIgnoreCase))
            return Silent;
        return SoundSchemes.Find(choice)?.Id ?? MatchTheme;
    }

    /// <summary>
    /// The scheme a typing sound choice plays under <paramref name="themeId"/>; null when it is silent.
    /// </summary>
    public static SoundScheme? ResolveTypingSound(string? choice, string? themeId) =>
        NormalizeTypingSound(choice) switch
        {
            Silent => null,
            MatchTheme => ThemeSoundSchemes.ForTheme(themeId),
            var id => SoundSchemes.Find(id),
        };
}

/// <summary>Reads and writes <see cref="SoundSettings"/> through <see cref="IAppSettingsStore"/>.</summary>
public sealed class SoundSettingsService
{
    private readonly IAppSettingsStore _store;

    public SoundSettingsService(IAppSettingsStore store)
    {
        _store = store;
    }

    /// <summary>The saved settings, or the defaults when none are saved or they can't be read.</summary>
    public async Task<SoundSettings> LoadAsync()
    {
        try
        {
            var saved = await _store.GetAsync<SoundSettings>(SoundSettings.StoreKey).ConfigureAwait(false);
            return (saved ?? new SoundSettings()).Normalized();
        }
        catch (System.Text.Json.JsonException)
        {
            return new SoundSettings();
        }
    }

    public Task SaveAsync(SoundSettings settings) =>
        _store.SetAsync(SoundSettings.StoreKey, settings.Normalized());
}
