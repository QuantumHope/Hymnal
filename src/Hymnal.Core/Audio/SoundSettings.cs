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

    /// <summary>Typewriter sounds on the Write surface. Off until the user turns them on.</summary>
    public bool TypingSoundsEnabled { get; init; }

    /// <summary>Typing sound volume, 0 to 1.</summary>
    public double TypingVolume { get; init; } = DefaultTypingVolume;

    /// <summary>A copy with every value in range (settings files can be hand-edited).</summary>
    public SoundSettings Normalized() => this with
    {
        TypingVolume = double.IsFinite(TypingVolume) ? Math.Clamp(TypingVolume, 0, 1) : DefaultTypingVolume,
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
