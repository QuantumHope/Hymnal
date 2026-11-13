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
    private bool _typingSoundsEnabled;
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

    public bool TypingSoundsEnabled
    {
        get => _typingSoundsEnabled;
        set
        {
            if (_typingSoundsEnabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _typingSoundsEnabled, value);
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

    /// <summary>Turns typing sounds on or off (View → Typewriter Sounds).</summary>
    public void ToggleTypingSounds() => TypingSoundsEnabled = !TypingSoundsEnabled;

    public SoundSettings ToSettings() => new()
    {
        TypingSoundsEnabled = TypingSoundsEnabled,
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
            TypingSoundsEnabled = saved.TypingSoundsEnabled;
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
