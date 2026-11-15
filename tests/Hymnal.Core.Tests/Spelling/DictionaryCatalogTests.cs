using Hymnal.Core.Spelling;

namespace Hymnal.Core.Tests.Spelling;

public sealed class DictionaryCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hymnal-dict-tests", Guid.NewGuid().ToString("N"));

    public DictionaryCatalogTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private void Install(string id)
    {
        File.Copy(Path.Combine(SpellCheckerTests.FixtureFolder, "xx-TEST.dic"), Path.Combine(_folder, id + ".dic"));
        File.Copy(Path.Combine(SpellCheckerTests.FixtureFolder, "xx-TEST.aff"), Path.Combine(_folder, id + ".aff"));
    }

    [Fact]
    public void Lists_user_dictionaries_with_both_files_and_language_names()
    {
        Install("en-GB");
        Install("fr_FR");
        File.WriteAllText(Path.Combine(_folder, "de-DE.dic"), "1\nwort\n"); // no .aff: not listed

        var available = new DictionaryCatalog(_folder, bundle: null).Available();

        Assert.Equal(["en-GB", "fr_FR"], available.Select(d => d.Id).OrderBy(i => i));
        Assert.All(available, d => Assert.Equal(DictionarySource.User, d.Source));
        Assert.Contains(available, d => d.DisplayName == "English (United Kingdom)");
    }

    [Fact]
    public void Missing_folder_or_bundle_means_no_dictionaries()
    {
        var catalog = new DictionaryCatalog(Path.Combine(_folder, "missing"), bundle: null);

        Assert.Empty(catalog.Available());
        Assert.Null(catalog.Resolve("en-US"));
    }

    [Fact]
    public void Resolves_exact_then_same_language_then_default_then_first()
    {
        Install("en-GB");
        Install("en-US");
        Install("fr-FR");
        var catalog = new DictionaryCatalog(_folder, bundle: null);

        Assert.Equal("fr-FR", catalog.Resolve("fr_FR")!.Id);   // _ and - are the same
        Assert.Equal("en-GB", catalog.Resolve("en-GB")!.Id);
        Assert.StartsWith("en-", catalog.Resolve("en-AU")!.Id); // same language
        Assert.Equal("en-US", catalog.Resolve("de-DE")!.Id);    // the default
        Assert.Equal("en-US", catalog.Resolve(null)!.Id);
    }

    [Fact]
    public void Falls_back_to_the_first_installed_when_the_default_is_missing()
    {
        Install("fr-FR");

        Assert.Equal("fr-FR", new DictionaryCatalog(_folder, bundle: null).Resolve("de-DE")!.Id);
    }

    [Fact]
    public async Task Loads_a_user_dictionary()
    {
        Install("xx-TEST");
        var catalog = new DictionaryCatalog(_folder, bundle: null);

        var loaded = await catalog.LoadAsync(catalog.Resolve("xx-TEST")!);

        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.True(loaded.Value!.Check("cat"));
        Assert.False(loaded.Value.Check("qwzx"));
    }

    [Fact]
    public async Task A_removed_dictionary_fails_to_load_instead_of_throwing()
    {
        Install("xx-TEST");
        var catalog = new DictionaryCatalog(_folder, bundle: null);
        var info = catalog.Resolve("xx-TEST")!;
        File.Delete(Path.Combine(_folder, "xx-TEST.aff"));

        var loaded = await catalog.LoadAsync(info);

        Assert.False(loaded.IsSuccess);
        Assert.Contains("no longer", loaded.Error);
    }

    [Fact]
    public async Task A_missing_bundled_dictionary_fails_to_load()
    {
        var catalog = new DictionaryCatalog(null, typeof(DictionaryCatalog).Assembly);

        var loaded = await catalog.LoadAsync(new DictionaryInfo("zz-ZZ", "Nowhere", DictionarySource.Bundled));

        Assert.False(loaded.IsSuccess);
    }

    [Theory]
    [InlineData("en-US", "English (United States)")]
    [InlineData("en_GB", "English (United Kingdom)")]
    [InlineData("not a culture!", "not a culture!")]
    public void Display_names_come_from_the_culture(string id, string expected)
    {
        Assert.Equal(expected, DictionaryCatalog.DisplayNameFor(id));
    }
}
