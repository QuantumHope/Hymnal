using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Theming;
using Hymnal.ViewModels;
using Hymnal.Views;
using Hymnal.Views.Controls;
using NSubstitute;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>A theme service that records what was picked, without touching the application.</summary>
public sealed class FakeThemes : IThemeService
{
    public IReadOnlyList<ThemeInfo> Available => ThemeCatalog.All;
    public ThemeInfo Current { get; private set; } = ThemeCatalog.Default;
    public List<string> Selected { get; } = [];

    public Task SelectAsync(string id)
    {
        Selected.Add(id);
        Current = ThemeCatalog.Find(id);
        return Task.CompletedTask;
    }
}

/// <summary>After OneRing's ThemeTests: the catalog, the preference, the token contract and the picker.</summary>
[Collection("AvaloniaUi")]
public sealed class ThemeTests
{
    static ThemeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    // ── Catalog ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-theme")]
    public void Unknown_theme_falls_back_to_synthwave(string? id) => Assert.Same(ThemeCatalog.Synthwave, ThemeCatalog.Find(id));

    [Fact]
    public void Theme_ids_match_case_insensitively() =>
        Assert.Same(ThemeCatalog.MordorScriptorium, ThemeCatalog.Find("MORDOR-Scriptorium"));

    [Fact]
    public void Catalog_has_hymnal_onering_and_every_cora_theme_but_no_brands()
    {
        Assert.Equal(26, ThemeCatalog.All.Count);
        Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var cora = ThemeCatalog.All.Where(t => t.Group == "CORA").ToList();
        Assert.Equal(24, cora.Count);
        Assert.Equal(2, cora.Count(t => t.HighContrast));
        Assert.Contains(cora, t => t.IsLight);
        Assert.Single(cora.Select(t => t.Styles).Distinct());
        Assert.DoesNotContain(ThemeCatalog.All, t =>
            t.Id.Contains("foxen", StringComparison.OrdinalIgnoreCase) || t.Id.Contains("edje", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bundled_fonts_leave_out_the_brand_only_faces()
    {
        var fonts = Directory.GetFiles(RepoPath("src/Hymnal/Assets/Fonts")).Select(Path.GetFileName).ToList();
        Assert.Contains("Audiowide-Regular.ttf", fonts);
        Assert.DoesNotContain(fonts, f => f!.StartsWith("Poppins") || f.StartsWith("Mulish") || f.StartsWith("LeagueSpartan"));
    }

    // ── Preference ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Preference_round_trips_through_the_app_settings()
    {
        var dir = Directory.CreateTempSubdirectory("hymnal-theme-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            using (var store = new AppSettingsStore(path))
            {
                await store.SetAsync("lastWorkspace", "/books/one");
                var pref = new ThemePreference(store);
                Assert.Null(await pref.LoadAsync());
                await pref.SaveAsync("cora-kitt");
                Assert.Equal("cora-kitt", await pref.LoadAsync());
                await store.FlushPendingAsync();
            }

            using var reopened = new AppSettingsStore(path);
            Assert.Equal("cora-kitt", await new ThemePreference(reopened).LoadAsync());
            Assert.Equal("/books/one", await reopened.GetAsync<string>("lastWorkspace"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_saved_uses_the_preference_and_falls_back_for_an_unknown_id()
    {
        var settings = Substitute.For<IAppSettingsStore>();
        settings.GetAsync<string>(ThemePreference.Key).Returns("cora-light", "retired-theme");
        var themes = new ThemeManager(new ThemePreference(settings));

        Assert.Same(ThemeCatalog.CoraLight, themes.ApplySaved());
        Assert.Same(ThemeCatalog.Synthwave, themes.ApplySaved());
    }

    // ── Token contract ──────────────────────────────────────────────────────

    [Fact]
    public void Every_theme_defines_the_same_keys() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var keySets = ThemeCatalog.All.ToDictionary(t => t.Id,
            t => ((ResourceDictionary)AvaloniaXamlLoader.Load(t.Tokens)).Keys.Select(k => k.ToString()!).ToHashSet());
        var cora = keySets[ThemeCatalog.CoraDark.Id];
        foreach (var (id, keys) in keySets)
        {
            // Themes add their own ornament colours; every CORA key must exist everywhere, except Chrome.* (a CORA
            // theme's private decoration, which only the thumbnail's face reads, with fallbacks).
            var missing = cora.Except(keys).Where(k => !k.StartsWith("Chrome.", StringComparison.Ordinal)).ToList();
            Assert.True(missing.Count == 0, $"{id} is missing: {string.Join(", ", missing)}");
        }
    });

    [Fact]
    public void Every_theme_defines_its_fluent_palette_as_colours() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        foreach (var t in ThemeCatalog.All)
        {
            var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(t.Tokens);
            foreach (var key in FluentKeys.All)
                Assert.True(tokens.TryGetResource(key, null, out var v) && v is Color, $"{t.Id}: {key} is not a Color");
        }
    });

    /// <summary>
    /// WCAG contrast on every theme's flat surfaces (OneRing's floors): body text 4.5:1 on every surface,
    /// secondary text 4.5:1 on panels, accents and chart series 3:1 (non-text).
    /// </summary>
    [Fact]
    public void Every_theme_meets_contrast_floors() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var failures = new List<string>();
        foreach (var t in ThemeCatalog.All)
        {
            var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(t.Tokens);
            Color C(string key) => tokens.TryGetResource(key, null, out var v)
                ? v switch { Color c => c, ISolidColorBrush b => b.Color, _ => throw new InvalidOperationException(key) }
                : throw new KeyNotFoundException($"{t.Id}: {key}");
            void Floor(string fg, string bg, double min)
            {
                var ratio = Contrast.Ratio(C(fg), C(bg));
                if (ratio < min) failures.Add($"{t.Id}: {fg} on {bg} is {ratio:0.00}:1, needs {min}:1");
            }
            foreach (var bg in new[] { "Cora.Surface.Void.Color", "Cora.Surface.Panel.Color", "Cora.Surface.Elevated.Color" })
                Floor("Cora.Text.Color", bg, 4.5);
            Floor("Cora.Text.Secondary.Color", "Cora.Surface.Panel.Color", 4.5);
            foreach (var accent in new[] { "Store", "Create", "Merge", "Retrieve", "Success", "Error", "Unknown" })
                Floor($"Cora.Accent.{accent}.Color", "Cora.Surface.Panel.Color", 3);
            for (var slot = 1; slot <= 7; slot++) Floor($"Cora.Viz.Series{slot}", "Cora.Surface.Panel.Color", 3);
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    [Fact]
    public void Contrast_ratio_matches_wcag() => Assert.Equal(21, Contrast.Ratio(Colors.Black, Colors.White), 3);

    // ── Applying ────────────────────────────────────────────────────────────

    [Fact]
    public void Applying_a_theme_swaps_tokens_and_recolours_brushes_by_key() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var app = Application.Current!;
        var themes = new ThemeManager();
        var brush = ThemeBrushes.Get("Cora.Accent.Store", "#123456");

        themes.Apply("synthwave");
        Assert.Equal(Color.Parse("#B469F0"), brush.Color);
        Assert.Equal(2, app.Resources.MergedDictionaries.Count); // the tokens and Hymnal's derived keys
        Assert.True(app.TryGetResource(HymnalTokens.Selection, null, out _));

        themes.Apply("cora-dark");
        Assert.Equal(Color.Parse("#F9A8C9"), brush.Color);
        Assert.Equal(2, app.Resources.MergedDictionaries.Count);
        Assert.Same(ThemeCatalog.CoraDark, themes.Current);
    });

    [Fact]
    public void Applying_a_light_theme_switches_fluent_to_light_and_swaps_theme_styles() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var app = Application.Current!;
        var themes = new ThemeManager();

        themes.Apply("synthwave");
        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        Assert.Single(app.Styles.OfType<StyleInclude>(), s => s.Source == ThemeCatalog.Synthwave.Styles);

        themes.Apply("cora-light");
        Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
        Assert.DoesNotContain(app.Styles.OfType<StyleInclude>(), s => s.Source == ThemeCatalog.Synthwave.Styles);
        Assert.Single(app.Styles.OfType<StyleInclude>(), s => s.Source == ThemeCatalog.CoraLight.Styles);

        themes.Apply("mordor-scriptorium");
        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        // Exactly one theme's styles at a time
        Assert.Single(app.Styles.OfType<StyleInclude>().Where(s => ThemeCatalog.All.Any(t => t.Styles == s.Source)),
            s => s.Source == ThemeCatalog.MordorScriptorium.Styles);
    });

    [Fact]
    public void Synthwave_dresses_the_bands_and_other_themes_keep_a_plain_rule() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var app = Application.Current!;
        // App.axaml's base band style, as the app has it
        app.Styles.Add(new Style(x => x.OfType<Border>().Class("band")) { Setters = { new Setter(Avalonia.Layout.Layoutable.HeightProperty, 1.0) } });
        var themes = new ThemeManager();
        var band = new Border { Classes = { "band", "top" } };
        var window = new Window { Content = band, Width = 200, Height = 40 };
        try
        {
            window.Show();
            Pump();
            themes.Apply("cora-dark");
            Pump();
            Assert.Equal(1, band.Height);
            themes.Apply("synthwave");
            Pump();
            Assert.Equal(3, band.Height);
            Assert.IsAssignableFrom<ILinearGradientBrush>(band.Background);
        }
        finally { window.Close(); }
    });

    // ── Thumbnails and picker ───────────────────────────────────────────────

    /// <summary>
    /// Every key the thumbnail draws with resolves in every theme's own tokens (so no tile falls back to gray), and
    /// drawing each tile runs cleanly. Hymnal's headless tests draw without Skia, so pixels can't be compared here.
    /// </summary>
    [Fact]
    public void Thumbnail_draws_each_theme_from_its_own_tokens() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        string[] keys =
        [
            "Cora.Surface.Window", "Cora.Surface.Panel", "Cora.Surface.Void", "Cora.Gradient.Header", "Cora.Gradient.Sidebar",
            "Cora.Border.Subtle", "Cora.Border.Input", "Cora.Border.Header", "Cora.Text", "Cora.Text.Secondary",
            "Cora.Wash.Selected", "Cora.Accent.Store", "Cora.Accent.Create", "Cora.Accent.Merge", "Cora.Accent.Retrieve",
            "Cora.Accent.Success", "Cora.Accent.Unknown",
        ];
        var size = new Size(104, 64);
        foreach (var t in ThemeCatalog.All)
        {
            var tokens = ThemeThumbnail.TokensFor(t);
            Assert.NotNull(tokens);
            foreach (var key in keys)
                Assert.True(tokens!.TryGetResource(key, t.Variant, out var v) && v is IBrush, $"{t.Id}: {key}");
            Assert.True(tokens!.TryGetResource("Cora.Font.Display", t.Variant, out var face) && face is FontFamily, $"{t.Id}: display face");

            var thumb = new ThemeThumbnail { ThemeInfo = t };
            thumb.Measure(size);
            thumb.Arrange(new Rect(size));
            using var bmp = new RenderTargetBitmap(new PixelSize(104, 64));
            using var ctx = bmp.CreateDrawingContext();
            thumb.Render(ctx);
        }
    });

    [Fact]
    public async Task Picker_groups_themes_and_marks_the_chosen_tile()
    {
        var themes = new FakeThemes();
        var vm = CreateSettings(themes);

        Assert.True(vm.CanPickTheme);
        // Light, then dark, as CORA groups its picker; where a theme comes from doesn't make a section
        Assert.Equal(["LIGHT THEMES", "DARK THEMES"], vm.ThemeSections.Select(x => x.Heading));
        Assert.Equal(ThemeCatalog.All.Count, vm.ThemeSections.Sum(x => x.Choices.Count));
        Assert.All(vm.ThemeSections[0].Choices, c => Assert.True(c.Theme.IsLight));
        Assert.All(vm.ThemeSections[1].Choices, c => Assert.False(c.Theme.IsLight));
        var dark = vm.ThemeSections[1].Choices.Select(c => c.Theme.DisplayName).ToList();
        Assert.Equal(dark.Order(StringComparer.CurrentCultureIgnoreCase), dark); // by name
        Assert.Contains("Synthwave", dark);
        Assert.Contains("Scriptorium of the Eye", dark);
        Assert.Same(ThemeCatalog.Synthwave, Assert.Single(vm.ThemeSections.SelectMany(x => x.Choices), c => c.IsCurrent).Theme);

        var kitt = vm.ThemeSections.SelectMany(x => x.Choices).Single(c => c.Theme.Id == "cora-kitt");
        await vm.ChooseThemeCommand.Execute(kitt.Theme);

        Assert.Equal(["cora-kitt"], themes.Selected);
        Assert.True(kitt.IsCurrent);
        Assert.Single(vm.ThemeSections.SelectMany(x => x.Choices), c => c.IsCurrent);
        Assert.Same(kitt.Theme, vm.SelectedTheme);
    }

    [Fact]
    public async Task Settings_shows_one_section_at_a_time()
    {
        var vm = CreateSettings(new FakeThemes());

        Assert.Equal(SettingsSection.Appearance, vm.Section);
        Assert.True(vm.IsAppearanceSection);
        Assert.False(vm.IsProjectSection || vm.IsAiProvidersSection);

        await vm.ShowSectionCommand.Execute(SettingsSection.AiProviders);
        Assert.True(vm.IsAiProvidersSection);
        Assert.False(vm.IsAppearanceSection || vm.IsProjectSection);
    }

    [Theory]
    [InlineData("synthwave", HymnalTokens.EmblemSun)]
    [InlineData("mordor-scriptorium", HymnalTokens.EmblemEye)]
    [InlineData("cora-kitt", HymnalTokens.EmblemFace)]
    [InlineData("cora-light", HymnalTokens.EmblemFace)]
    public void Each_theme_gets_its_emblem(string id, string emblem) => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(ThemeCatalog.Find(id).Tokens);
        Assert.True(HymnalTokens.DeriveFrom(tokens).TryGetResource(HymnalTokens.Emblem, null, out var kind));
        Assert.Equal(emblem, kind);
    });

    [Fact]
    public void Every_cora_theme_brings_its_motto_and_chrome() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        foreach (var t in ThemeCatalog.All.Where(t => t.Group == "CORA"))
        {
            var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(t.Tokens);
            Assert.True(tokens.TryGetResource("Theme.Text.Motto", null, out var motto) && motto is string, $"{t.Id}: motto");
            Assert.True(tokens.TryGetResource("Theme.Chrome", null, out var chrome) && chrome is string, $"{t.Id}: chrome");
        }
    });

    [Fact]
    public void Settings_hides_the_picker_without_a_theme_service()
    {
        var vm = CreateSettings(null);
        Assert.False(vm.CanPickTheme);
        Assert.Empty(vm.ThemeSections);
    }

    [Fact]
    public void Gallery_tiles_are_one_radio_group_with_the_applied_theme_checked() => ReactiveUiTestBootstrap.RunOnUiThread(() =>
    {
        var themes = new FakeThemes();
        var vm = CreateSettings(themes);
        var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1200, Height = 900 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var tiles = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window)
                .OfType<RadioButton>().Where(r => r.Classes.Contains("themeTile")).ToList();
            Assert.Equal(ThemeCatalog.All.Count, tiles.Count);
            Assert.All(tiles, t => Assert.Equal("theme", t.GroupName));
            var on = Assert.Single(tiles, t => t.IsChecked == true);
            Assert.Equal("Synthwave", Avalonia.Automation.AutomationProperties.GetName(on));
        }
        finally { window.Close(); }
    });

    // ── Guard ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Views take colours from the theme, so a light theme can't leave dark-theme literals behind. Shadows (black
    /// with alpha) are allowed; token and theme-style files are where colours belong.
    /// </summary>
    [Fact]
    public void Views_use_theme_tokens_rather_than_literal_colours()
    {
        var views = RepoPath("src/Hymnal/Views");
        var literal = new Regex("\"#(?![0-9A-Fa-f]{2}000000\")[0-9A-Fa-f]{6,8}\"|=\"(White|Black)\"");
        var offenders = Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Append(RepoPath("src/Hymnal/App.axaml"))
            .Append(RepoPath("src/Hymnal/Themes/ControlStyles.axaml"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => literal.IsMatch(x.line) && !x.line.Contains("BoxShadow"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Text on an accent fill uses <c>Cora.IconInk</c>: <c>Cora.Text</c> on <c>Cora.Accent.Store</c> falls far below
    /// legible contrast in some themes (1.91:1 in Scriptorium of the Eye). Checks elements and styles alike.
    /// </summary>
    [Fact]
    public void Text_on_an_accent_fill_uses_the_icon_ink()
    {
        const string accentFill = "{DynamicResource Cora.Accent.Store}";
        const string plainText = "{DynamicResource Cora.Text}";
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPath("src/Hymnal"), "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in Regex.Matches(text, @"<[A-Za-z][^<>]*?>", RegexOptions.Singleline))
                if (tag.Value.Contains($"Background=\"{accentFill}\"") && tag.Value.Contains($"Foreground=\"{plainText}\""))
                    offenders.Add($"{Path.GetFileName(file)}:{text[..tag.Index].Count(c => c == '\n') + 1}: element");
            foreach (Match style in Regex.Matches(text, @"<Style\b[^>]*>.*?</Style>", RegexOptions.Singleline))
                if (style.Value.Contains($"Property=\"Background\" Value=\"{accentFill}\"")
                    && style.Value.Contains($"Property=\"Foreground\" Value=\"{plainText}\""))
                    offenders.Add($"{Path.GetFileName(file)}:{text[..style.Index].Count(c => c == '\n') + 1}: style");
        }
        Assert.True(offenders.Count == 0, "Use Cora.IconInk for text on Cora.Accent.Store:" + Environment.NewLine
                                          + string.Join(Environment.NewLine, offenders));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static SettingsViewModel CreateSettings(IThemeService? themes) => new(
        new AiTestDoubles.InMemoryProviderProfileService(),
        new AiTestDoubles.InMemoryCredentialStore(),
        new AiTestDoubles.NoOpAiChatService(),
        Substitute.For<INotificationService>(),
        themes: themes);

    private static string RepoPath(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hymnal.slnx")))
                return Path.Combine(dir.FullName, relative);
        throw new DirectoryNotFoundException("Hymnal.slnx not found above " + AppContext.BaseDirectory);
    }
}
