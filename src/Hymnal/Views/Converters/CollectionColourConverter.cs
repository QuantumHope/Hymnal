using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Hymnal.Theming;

namespace Hymnal.Views.Converters;

/// <summary>
/// A collection's colour on the story planner: its colour index (1–7) as the theme's chart series brush
/// (<c>Cora.Viz.Series{n}</c>), recoloured on a theme change. The fallbacks are Synthwave's values.
/// </summary>
public sealed class CollectionColourConverter : IValueConverter
{
    public static readonly CollectionColourConverter Instance = new();

    private static readonly string[] Fallbacks =
        ["#3987E5", "#D95926", "#199E70", "#C98500", "#D55181", "#008300", "#9085E9"];

    public static IBrush For(int index)
    {
        var i = ((index - 1) % Fallbacks.Length + Fallbacks.Length) % Fallbacks.Length;
        return ThemeBrushes.Get($"Cora.Viz.Series{i + 1}", Fallbacks[i]);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? For(index) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
