using Hymnal.Core.Spelling;
using Hymnal.Core.Tests.Audio;
using Hymnal.Spelling;

namespace Hymnal.Core.Tests.Spelling;

public sealed class SpellCheckSettingsTests
{
    [Fact]
    public async Task Defaults_keep_spell_check_out_of_the_way()
    {
        var loaded = await new SpellCheckSettingsService(new TypingSoundPlayerTests.InMemorySoundStore()).LoadAsync();

        Assert.False(loaded.CheckAsYouType);
        Assert.True(loaded.HideInFocusMode);
        Assert.Equal("en-US", loaded.Language);
    }

    [Fact]
    public async Task Saves_and_loads_under_the_spelling_key()
    {
        var store = new TypingSoundPlayerTests.InMemorySoundStore();
        var service = new SpellCheckSettingsService(store);

        await service.SaveAsync(new SpellCheckSettings { CheckAsYouType = true, HideInFocusMode = false, Language = " en-GB " });
        var loaded = await service.LoadAsync();

        Assert.True(loaded.CheckAsYouType);
        Assert.False(loaded.HideInFocusMode);
        Assert.Equal("en-GB", loaded.Language);
        Assert.NotNull(await store.GetAsync<SpellCheckSettings>(SpellCheckSettings.StoreKey));
    }

    [Fact]
    public void A_blank_language_normalizes_to_the_default()
    {
        Assert.Equal("en-US", new SpellCheckSettings { Language = "  " }.Normalized().Language);
    }

    [Fact]
    public async Task Preferences_load_saved_values_and_save_changes()
    {
        var store = new TypingSoundPlayerTests.InMemorySoundStore();
        await new SpellCheckSettingsService(store).SaveAsync(new SpellCheckSettings { Language = "en-GB" });

        var prefs = new SpellCheckPreferences(new SpellCheckSettingsService(store));
        await prefs.Loaded;
        Assert.Equal("en-GB", prefs.Language);
        Assert.False(prefs.CheckAsYouType);

        prefs.ToggleCheckAsYouType();
        prefs.HideInFocusMode = false;

        var reloaded = new SpellCheckPreferences(new SpellCheckSettingsService(store));
        await reloaded.Loaded;
        Assert.True(reloaded.CheckAsYouType);
        Assert.False(reloaded.HideInFocusMode);
        Assert.Equal("en-GB", reloaded.Language);
    }
}
