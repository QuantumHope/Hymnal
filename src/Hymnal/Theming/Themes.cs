using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Hymnal.Core.Interfaces;

namespace Hymnal.Theming;

// After OneRing's Services/Themes.cs. Every theme is one token file with the same Cora.* keys (CORA's vocabulary),
// plus optional control styles; nothing visual lives in C#. When OneRing and Hymnal share an Eland.Themes package,
// this file is what moves into it.

/// <summary>
/// A theme: its token dictionary (same keys in every theme) and optional extra control styles. Everything visual,
/// including the Fluent palette that tints the built-in controls, lives in the token file; this record only says
/// where the files are and what to call the theme.
/// </summary>
public sealed record ThemeInfo(
    string Id,
    string DisplayName,
    string Description,
    Uri Tokens,
    Uri? Styles,
    bool IsLight = false)
{
    /// <summary>Where the theme comes from, for the picker's sections: Hymnal, OneRing or CORA.</summary>
    public string Group { get; init; } = "Hymnal";
    /// <summary>CORA's icon name for the theme (kept from the import; Hymnal's picker doesn't show it).</summary>
    public string? Icon { get; init; }
    public bool HighContrast { get; init; }

    /// <summary>The Fluent variant underneath: a light theme switches Fluent's own controls to their light templates.</summary>
    public ThemeVariant Variant => IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

    public override string ToString() => DisplayName;
}

/// <summary>
/// The Fluent palette colours every token file defines (as <c>Color</c> resources), so a theme is one file plus,
/// optionally, its control styles.
/// </summary>
public static class FluentKeys
{
    public const string Accent = "Fluent.Accent";
    public const string Region = "Fluent.Region";
    public const string BaseHigh = "Fluent.BaseHigh";
    public const string ChromeMedium = "Fluent.ChromeMedium";
    public const string ChromeMediumLow = "Fluent.ChromeMediumLow";
    public const string ChromeLow = "Fluent.ChromeLow";
    public const string ErrorText = "Fluent.ErrorText";

    public static IReadOnlyList<string> All { get; } =
        [Accent, Region, BaseHigh, ChromeMedium, ChromeMediumLow, ChromeLow, ErrorText];
}

public static partial class ThemeCatalog
{
    /// <summary>What a fresh install, an empty preference or an unknown id gets: Hymnal's own look.</summary>
    public const string DefaultId = "synthwave";

    private static Uri Theme(string file) => new($"avares://Hymnal/Themes/{file}.axaml");

    public static readonly ThemeInfo Synthwave = new(
        DefaultId, "Synthwave",
        "Hymnal's neon: violet night, a magenta-to-cyan horizon, and a neon band above and below the page.",
        Theme("Synthwave.Dark"), Theme("Synthwave.Controls"));

    public static readonly ThemeInfo MordorScriptorium = new(
        "mordor-scriptorium", "Scriptorium of the Eye",
        "From OneRing: Mordor's ember and obsidian, a scriptorium's vellum and gold leaf.",
        Theme("Mordor.Dark"), Theme("Mordor.Controls")) { Group = "OneRing" };

    public static ThemeInfo Default => Synthwave;

    /// <summary>CORA's own dark and light themes, imported with the rest (CoraThemes.g.cs).</summary>
    public static ThemeInfo CoraDark => Find("cora-dark");
    public static ThemeInfo CoraLight => Find("cora-light");

    /// <summary>Every theme: Hymnal's own, OneRing's, then CORA's in its registry order.</summary>
    public static IReadOnlyList<ThemeInfo> All { get; }

    // Built here rather than in an initializer: CoraThemes lives in another part of this class, and the order
    // initializers run in across partial files is not defined.
    static ThemeCatalog() => All = [Synthwave, MordorScriptorium, .. CoraThemes];

    /// <summary>The theme with this id, or the default for an unknown or empty id.</summary>
    public static ThemeInfo Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Synthwave;
}

/// <summary>The chosen theme's id, kept in Hymnal's app settings (OS AppData) under <see cref="Key"/>.</summary>
public sealed class ThemePreference(IAppSettingsStore settings)
{
    public const string Key = "theme";

    public async Task<string?> LoadAsync()
    {
        try { return await settings.GetAsync<string>(Key).ConfigureAwait(false); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(string themeId) => settings.SetAsync(Key, themeId);
}

/// <summary>What the Settings screen needs to list and pick themes (a fake in tests).</summary>
public interface IThemeService
{
    IReadOnlyList<ThemeInfo> Available { get; }
    ThemeInfo Current { get; }
    /// <summary>Applies the theme now and remembers it for the next start.</summary>
    Task SelectAsync(string id);
}

/// <summary>Swaps the application's theme tokens, theme styles and Fluent palette while the app runs.</summary>
public sealed class ThemeManager(ThemePreference? preference = null) : IThemeService
{
    private static readonly Uri BaseUri = new("avares://Hymnal/App.axaml");
    /// <summary>A key every theme's token dictionary defines, and nothing else does.</summary>
    public const string MarkerKey = FluentKeys.Accent;

    public IReadOnlyList<ThemeInfo> Available => ThemeCatalog.All;
    public ThemeInfo Current { get; private set; } = ThemeCatalog.Default;
    public event Action<ThemeInfo>? Changed;

    /// <summary>
    /// The saved theme (or the default), applied on the calling (UI) thread. Blocks briefly on the settings read,
    /// which runs on the thread pool so the wait can't deadlock the UI thread.
    /// </summary>
    public ThemeInfo ApplySaved()
    {
        var id = preference is null ? null : Task.Run(preference.LoadAsync).GetAwaiter().GetResult();
        return Apply(id);
    }

    public async Task SelectAsync(string id)
    {
        var t = Apply(id);
        if (preference is not null) await preference.SaveAsync(t.Id);
    }

    public ThemeInfo Apply(string? id)
    {
        var theme = ThemeCatalog.Find(id);
        if (Application.Current is not { } app) return theme;

        // Find theme token dictionaries by a key every theme defines: the one App.axaml includes is compiled in,
        // so it is a plain ResourceDictionary at runtime, not a ResourceInclude with a Source to compare.
        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
            if (merged[i].TryGetResource(MarkerKey, null, out _) || merged[i].TryGetResource(HymnalTokens.MarkerKey, null, out _))
                merged.RemoveAt(i);
        var tokens = new ResourceInclude(BaseUri) { Source = theme.Tokens };
        merged.Add(tokens);
        merged.Add(HymnalTokens.DeriveFrom(tokens));

        for (var i = app.Styles.Count - 1; i >= 0; i--)
            if (app.Styles[i] is StyleInclude si && ThemeCatalog.All.Any(t => t.Styles is not null && t.Styles == si.Source))
                app.Styles.RemoveAt(i);
        if (theme.Styles is not null)
            app.Styles.Add(new StyleInclude(BaseUri) { Source = theme.Styles });

        // Light themes switch Fluent to its light templates; the palette for that variant carries the theme's colours.
        app.RequestedThemeVariant = theme.Variant;
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            if (!fluent.Palettes.TryGetValue(theme.Variant, out var p))
                fluent.Palettes[theme.Variant] = p = new ColorPaletteResources();
            Color C(string key) => FluentColor(tokens, key);
            p.Accent = C(FluentKeys.Accent);
            p.RegionColor = C(FluentKeys.Region);
            p.AltHigh = C(FluentKeys.Region);
            p.BaseHigh = C(FluentKeys.BaseHigh);
            p.ChromeMedium = C(FluentKeys.ChromeMedium);
            p.ChromeMediumLow = C(FluentKeys.ChromeMediumLow);
            p.ChromeLow = C(FluentKeys.ChromeLow);
            p.ErrorText = C(FluentKeys.ErrorText);
        }

        ThemeBrushes.Refresh();
        Current = theme;
        Changed?.Invoke(theme);
        return theme;
    }

    /// <summary>A Fluent palette colour from a theme's tokens. Gray only if a token file forgot one (a test catches that).</summary>
    public static Color FluentColor(IResourceProvider tokens, string key) =>
        tokens.TryGetResource(key, null, out var v) && v is Color c ? c : Colors.Gray;
}

/// <summary>
/// The few keys Hymnal needs beyond the shared contract, worked out from each theme's own tokens so the token files
/// stay identical to OneRing's.
/// </summary>
public static class HymnalTokens
{
    /// <summary>Marks the derived dictionary so the next theme change replaces it.</summary>
    public const string MarkerKey = "Hymnal.Derived";
    /// <summary>Text selection in editors and text boxes: the theme's Store accent at 40%.</summary>
    public const string Selection = "Hymnal.Selection";

    /// <summary>Which emblem the theme draws (ThemeEmblem): <see cref="EmblemSun"/>, <see cref="EmblemEye"/>, <see cref="EmblemFace"/> or none.</summary>
    public const string Emblem = "Hymnal.Emblem";
    public const string EmblemSun = "sun";
    public const string EmblemEye = "eye";
    public const string EmblemFace = "face";
    public const string EmblemNone = "none";

    public static ResourceDictionary DeriveFrom(IResourceProvider tokens)
    {
        bool Has(string key) => tokens.TryGetResource(key, null, out _);
        var store = tokens.TryGetResource("Cora.Accent.Store.Color", null, out var v) && v is Color c ? c : Color.Parse("#B469F0");
        return new ResourceDictionary
        {
            [MarkerKey] = true,
            [Selection] = new SolidColorBrush(Color.FromArgb(0x66, store.R, store.G, store.B)),
            // Synthwave has its sun, Mordor its Eye, and a CORA theme with a signature her face
            [Emblem] = Has("Synthwave.Sun") ? EmblemSun
                : Has("Mordor.Gold") ? EmblemEye
                : Has("Chrome.Her.Face") ? EmblemFace
                : EmblemNone,
        };
    }
}

/// <summary>
/// Stable brushes for code that picks a theme colour by key (converters, drawn controls, view models). The brush
/// objects never change; their colours are updated when the theme changes, so already-bound rows recolour without
/// being rebuilt. The fallback is used when no theme is loaded (headless tests) and should be Synthwave's value.
/// </summary>
public static class ThemeBrushes
{
    private static readonly Dictionary<(string Key, Color Fallback), SolidColorBrush> Cache = [];

    /// <summary>A brush that follows the theme's colour for <paramref name="key"/>.</summary>
    public static SolidColorBrush Get(string key, Color fallback)
    {
        lock (Cache)
        {
            // A brush belongs to the UI thread that made it. The app has one; headless tests start a new one per
            // test, so a brush left by an earlier test is replaced rather than touched.
            if (!Cache.TryGetValue((key, fallback), out var b) || !b.CheckAccess())
                Cache[(key, fallback)] = b = new SolidColorBrush(Lookup(key, fallback));
            return b;
        }
    }

    /// <inheritdoc cref="Get(string, Color)"/>
    public static SolidColorBrush Get(string key, string fallbackHex) => Get(key, Avalonia.Media.Color.Parse(fallbackHex));

    /// <summary>The theme's colour for <paramref name="key"/> right now (for drawing code that renders on demand).</summary>
    public static Color Color(string key, Color fallback) => Lookup(key, fallback);

    /// <inheritdoc cref="Color(string, Avalonia.Media.Color)"/>
    public static Color Color(string key, string fallbackHex) => Lookup(key, Avalonia.Media.Color.Parse(fallbackHex));

    public static void Refresh()
    {
        lock (Cache)
            foreach (var ((key, fallback), brush) in Cache)
                if (brush.CheckAccess()) brush.Color = Lookup(key, fallback);
    }

    private static Color Lookup(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var v))
            return v switch
            {
                ISolidColorBrush s => s.Color,
                Color c => c,
                IGradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
                _ => fallback,
            };
        return fallback;
    }
}

/// <summary>WCAG 2.x contrast between two colours (alpha ignored), for checking a theme's token pairs.</summary>
public static class Contrast
{
    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
