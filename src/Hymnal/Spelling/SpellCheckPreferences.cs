using System.Threading.Tasks;
using Hymnal.Core.Spelling;
using ReactiveUI;

namespace Hymnal.Spelling;

/// <summary>
/// The live, app-wide spell check preferences (one instance, shared by Settings, the View menu and the editor).
/// Loads <see cref="SpellCheckSettings"/> once at start-up and saves every change. Mirrors
/// <see cref="Hymnal.Audio.SoundPreferences"/>.
/// </summary>
public sealed class SpellCheckPreferences : ReactiveObject
{
    private readonly SpellCheckSettingsService? _service;
    private bool _checkAsYouType;
    private bool _hideInFocusMode = true;
    private string _language = SpellCheckSettings.DefaultLanguage;
    private bool _changedBeforeLoad;
    private bool _applyingLoaded;

    public SpellCheckPreferences(SpellCheckSettingsService? service)
    {
        _service = service;
        Loaded = service is null ? Task.CompletedTask : LoadAsync();
    }

    /// <summary>Completes once the saved settings are applied.</summary>
    public Task Loaded { get; }

    /// <summary>Mark misspellings continuously (View → Spelling → Check As You Type).</summary>
    public bool CheckAsYouType
    {
        get => _checkAsYouType;
        set
        {
            if (_checkAsYouType == value)
                return;
            this.RaiseAndSetIfChanged(ref _checkAsYouType, value);
            OnChanged();
        }
    }

    /// <summary>Hide spelling marks while Focus Mode is on.</summary>
    public bool HideInFocusMode
    {
        get => _hideInFocusMode;
        set
        {
            if (_hideInFocusMode == value)
                return;
            this.RaiseAndSetIfChanged(ref _hideInFocusMode, value);
            OnChanged();
        }
    }

    /// <summary>The dictionary id, such as <c>en-US</c>.</summary>
    public string Language
    {
        get => _language;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? SpellCheckSettings.DefaultLanguage : value.Trim();
            if (_language == value)
                return;
            this.RaiseAndSetIfChanged(ref _language, value);
            OnChanged();
        }
    }

    /// <summary>Turns checking as you type on or off.</summary>
    public void ToggleCheckAsYouType() => CheckAsYouType = !CheckAsYouType;

    public SpellCheckSettings ToSettings() => new()
    {
        CheckAsYouType = CheckAsYouType,
        HideInFocusMode = HideInFocusMode,
        Language = Language,
    };

    private async Task LoadAsync()
    {
        var saved = await _service!.LoadAsync().ConfigureAwait(true);

        // A change the user made while the file was still loading wins over what was saved.
        if (_changedBeforeLoad)
            return;

        _applyingLoaded = true;
        try
        {
            Language = saved.Language;
            HideInFocusMode = saved.HideInFocusMode;
            CheckAsYouType = saved.CheckAsYouType;
        }
        finally
        {
            _applyingLoaded = false;
        }
    }

    private void OnChanged()
    {
        if (_applyingLoaded || _service is null)
            return;

        if (!Loaded.IsCompleted)
            _changedBeforeLoad = true;

        _ = _service.SaveAsync(ToSettings());
    }
}
