using System;
using System.Threading.Tasks;
using Hymnal.Core.Audio;
using ReactiveUI;

namespace Hymnal.Audio;

/// <summary>
/// The live, app-wide sound preferences (one instance, shared by Settings, the View menu and the players).
/// Loads <see cref="SoundSettings"/> once at start-up and saves every change.
/// </summary>
public sealed class SoundPreferences : ReactiveObject
{
    private readonly SoundSettingsService _service;
    private bool _soundEnabled;
    private string _typingSound = SoundSettings.MatchTheme;
    private double _typingVolume = SoundSettings.DefaultTypingVolume;
    private bool _changedBeforeLoad;
    private bool _applyingLoaded;

    public SoundPreferences(SoundSettingsService service)
    {
        _service = service;
        Loaded = LoadAsync();
    }

    /// <summary>Completes once the saved settings are applied.</summary>
    public Task Loaded { get; }

    /// <summary>Whether Hymnal makes any sound (Settings → Sound, View → Sound).</summary>
    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            if (_soundEnabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _soundEnabled, value);
            OnChanged();
        }
    }

    /// <summary>
    /// The typing sound: <see cref="SoundSettings.MatchTheme"/>, <see cref="SoundSettings.Silent"/>, or a scheme id.
    /// </summary>
    public string TypingSound
    {
        get => _typingSound;
        set
        {
            value = SoundSettings.NormalizeTypingSound(value);
            if (_typingSound == value)
                return;
            this.RaiseAndSetIfChanged(ref _typingSound, value);
            OnChanged();
        }
    }

    /// <summary>Typing sound volume, 0 to 1.</summary>
    public double TypingVolume
    {
        get => _typingVolume;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : SoundSettings.DefaultTypingVolume;
            if (Math.Abs(_typingVolume - value) < 1e-9)
                return;
            this.RaiseAndSetIfChanged(ref _typingVolume, value);
            OnChanged();
        }
    }

    /// <summary>Turns sound on or off (View → Sound).</summary>
    public void ToggleSound() => SoundEnabled = !SoundEnabled;

    public SoundSettings ToSettings() => new()
    {
        SoundEnabled = SoundEnabled,
        TypingSound = TypingSound,
        TypingVolume = TypingVolume,
    };

    private async Task LoadAsync()
    {
        var saved = await _service.LoadAsync().ConfigureAwait(true);

        // A change the user made while the file was still loading wins over what was saved.
        if (_changedBeforeLoad)
            return;

        _applyingLoaded = true;
        try
        {
            TypingVolume = saved.TypingVolume;
            TypingSound = saved.TypingSound;
            SoundEnabled = saved.SoundEnabled;
        }
        finally
        {
            _applyingLoaded = false;
        }
    }

    private void OnChanged()
    {
        if (_applyingLoaded)
            return;

        if (!Loaded.IsCompleted)
            _changedBeforeLoad = true;

        _ = _service.SaveAsync(ToSettings());
    }
}
