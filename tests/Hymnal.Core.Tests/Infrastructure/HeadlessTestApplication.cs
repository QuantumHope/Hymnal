using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Hymnal.Theming;

namespace Hymnal.Core.Tests.Infrastructure;

internal sealed class HeadlessTestApplication : Application
{
    public HeadlessTestApplication()
    {
        Styles.Add(new FluentTheme());

        // The default theme's tokens, as the app always has them: views take every colour from the theme, and a
        // control whose background is unresolved isn't hit-testable, so clicks in UI tests would fall through.
        var tokens = new ResourceInclude(new Uri("avares://Hymnal/App.axaml")) { Source = ThemeCatalog.Default.Tokens };
        Resources.MergedDictionaries.Add(tokens);
        Resources.MergedDictionaries.Add(HymnalTokens.DeriveFrom(tokens));
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<HeadlessTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
