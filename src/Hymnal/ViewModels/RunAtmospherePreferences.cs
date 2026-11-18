using System;
using System.Text.Json;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models.Runs;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// The live, app-wide run atmosphere choices (Settings → Runs). Loads <see cref="RunAtmosphereSettings"/> once and
/// saves every change.
/// </summary>
public sealed class RunAtmospherePreferences : ReactiveObject
{
    private readonly IAppSettingsStore? _store;
    private bool _applyingLoaded;
    private bool _changedBeforeLoad;

    private bool _flames = true;
    private bool _edgeGlow = true;
    private bool _sparks = true;
    private bool _ambience = true;
    private double _ambienceVolume = RunAtmosphereSettings.DefaultAmbienceVolume;

    /// <summary>With no store (tests), every layer is on and nothing is saved.</summary>
    public RunAtmospherePreferences(IAppSettingsStore? store = null)
    {
        _store = store;
        Loaded = store == null ? Task.CompletedTask : LoadAsync();
    }

    public Task Loaded { get; }

    public bool Flames
    {
        get => _flames;
        set { if (_flames != value) { this.RaiseAndSetIfChanged(ref _flames, value); OnChanged(); } }
    }

    public bool EdgeGlow
    {
        get => _edgeGlow;
        set { if (_edgeGlow != value) { this.RaiseAndSetIfChanged(ref _edgeGlow, value); OnChanged(); } }
    }

    public bool Sparks
    {
        get => _sparks;
        set { if (_sparks != value) { this.RaiseAndSetIfChanged(ref _sparks, value); OnChanged(); } }
    }

    public bool Ambience
    {
        get => _ambience;
        set { if (_ambience != value) { this.RaiseAndSetIfChanged(ref _ambience, value); OnChanged(); } }
    }

    public double AmbienceVolume
    {
        get => _ambienceVolume;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : RunAtmosphereSettings.DefaultAmbienceVolume;
            if (Math.Abs(_ambienceVolume - value) < 1e-9)
                return;
            this.RaiseAndSetIfChanged(ref _ambienceVolume, value);
            OnChanged();
        }
    }

    public RunAtmosphereSettings ToSettings() => new()
    {
        Flames = Flames,
        EdgeGlow = EdgeGlow,
        Sparks = Sparks,
        Ambience = Ambience,
        AmbienceVolume = AmbienceVolume,
    };

    private async Task LoadAsync()
    {
        RunAtmosphereSettings saved;
        try
        {
            saved = (await _store!.GetAsync<RunAtmosphereSettings>(RunAtmosphereSettings.StoreKey).ConfigureAwait(true)
                     ?? new RunAtmosphereSettings()).Normalized();
        }
        catch (JsonException)
        {
            saved = new RunAtmosphereSettings();
        }

        if (_changedBeforeLoad)
            return;

        _applyingLoaded = true;
        try
        {
            Flames = saved.Flames;
            EdgeGlow = saved.EdgeGlow;
            Sparks = saved.Sparks;
            Ambience = saved.Ambience;
            AmbienceVolume = saved.AmbienceVolume;
        }
        finally
        {
            _applyingLoaded = false;
        }
    }

    private void OnChanged()
    {
        if (_applyingLoaded || _store == null)
            return;
        if (!Loaded.IsCompleted)
            _changedBeforeLoad = true;
        _ = _store.SetAsync(RunAtmosphereSettings.StoreKey, ToSettings().Normalized());
    }
}
