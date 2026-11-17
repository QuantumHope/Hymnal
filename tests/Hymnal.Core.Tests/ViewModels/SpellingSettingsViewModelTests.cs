using System.Reactive.Linq;
using Hymnal.Core.Spelling;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.Spelling;

namespace Hymnal.ViewModels;

/// <summary>Settings → Spelling: the dictionary choice and the project dictionary.</summary>
[Collection("AvaloniaUi")]
public sealed class SpellingSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-spelling-settings-tests", Guid.NewGuid().ToString("N"));

    static SpellingSettingsViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public SpellingSettingsViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Lists_dictionaries_and_saves_the_choice()
    {
        var folder = Path.Combine(_root, "dicts");
        Directory.CreateDirectory(folder);
        foreach (var id in new[] { "en-GB", "xx-TEST" })
        {
            File.Copy(Path.Combine(SpellCheckerTests.FixtureFolder, "xx-TEST.dic"), Path.Combine(folder, id + ".dic"));
            File.Copy(Path.Combine(SpellCheckerTests.FixtureFolder, "xx-TEST.aff"), Path.Combine(folder, id + ".aff"));
        }

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(() =>
        {
            using var service = EditorSpellingViewModelTests.CreateService(folder);
            using var settings = new SpellingSettingsViewModel(service);

            Assert.True(settings.HasLanguages);
            Assert.Equal("xx-TEST", settings.SelectedLanguage!.Id);

            settings.SelectedLanguage = settings.Languages.Single(l => l.Id == "en-GB");
            Assert.Equal("en-GB", service.Preferences.Language);
            return Task.FromResult(0);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Adds_and_removes_project_words_only_with_a_workspace()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var service = EditorSpellingViewModelTests.CreateService();
            using var settings = new SpellingSettingsViewModel(service);

            settings.NewWord = "Elara";
            Assert.False(settings.HasWorkspace);
            Assert.False(await settings.AddWordCommand.CanExecute.FirstAsync());

            await service.SetWorkspaceAsync(_root);
            Assert.True(settings.ShowNoProjectWords);
            Assert.True(await settings.AddWordCommand.CanExecute.FirstAsync());

            await settings.AddWordCommand.Execute();
            Assert.Equal(["Elara"], settings.ProjectWords);
            Assert.Equal("", settings.NewWord);
            Assert.True(settings.HasProjectWords);

            settings.NewWord = "two words";
            Assert.False(await settings.AddWordCommand.CanExecute.FirstAsync());

            await settings.RemoveWordCommand.Execute("Elara");
            Assert.Empty(settings.ProjectWords);
            Assert.True(settings.ShowNoProjectWords);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }
}
