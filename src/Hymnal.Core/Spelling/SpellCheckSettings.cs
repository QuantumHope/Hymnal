using Hymnal.Core.Interfaces;

namespace Hymnal.Core.Spelling;

/// <summary>
/// App-wide spell check preferences, stored in the app settings under <see cref="StoreKey"/>.
/// <para>
/// Spell check stays out of the way by default: nothing is marked until the writer asks for a check
/// (View → Spelling → Check Spelling) or turns on <see cref="CheckAsYouType"/>.
/// </para>
/// </summary>
public sealed record SpellCheckSettings
{
    public const string StoreKey = "spelling";

    /// <summary>The language used when none is saved, or the saved one is no longer installed.</summary>
    public const string DefaultLanguage = "en-US";

    /// <summary>Mark misspellings continuously while writing. Off until the writer turns it on.</summary>
    public bool CheckAsYouType { get; init; }

    /// <summary>Hide spelling marks while Focus Mode is on, so a drafting session stays quiet.</summary>
    public bool HideInFocusMode { get; init; } = true;

    /// <summary>The dictionary to check against, as a <see cref="DictionaryInfo.Id"/> such as <c>en-US</c>.</summary>
    public string Language { get; init; } = DefaultLanguage;

    /// <summary>A copy with every value usable (settings files can be hand-edited).</summary>
    public SpellCheckSettings Normalized() => this with
    {
        Language = string.IsNullOrWhiteSpace(Language) ? DefaultLanguage : Language.Trim(),
    };
}

/// <summary>Reads and writes <see cref="SpellCheckSettings"/> through <see cref="IAppSettingsStore"/>.</summary>
public sealed class SpellCheckSettingsService
{
    private readonly IAppSettingsStore _store;

    public SpellCheckSettingsService(IAppSettingsStore store)
    {
        _store = store;
    }

    /// <summary>The saved settings, or the defaults when none are saved or they can't be read.</summary>
    public async Task<SpellCheckSettings> LoadAsync()
    {
        try
        {
            var saved = await _store.GetAsync<SpellCheckSettings>(SpellCheckSettings.StoreKey).ConfigureAwait(false);
            return (saved ?? new SpellCheckSettings()).Normalized();
        }
        catch (System.Text.Json.JsonException)
        {
            return new SpellCheckSettings();
        }
    }

    public Task SaveAsync(SpellCheckSettings settings) =>
        _store.SetAsync(SpellCheckSettings.StoreKey, settings.Normalized());
}
