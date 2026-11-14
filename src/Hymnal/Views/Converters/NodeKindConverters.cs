using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.Theming;

namespace Hymnal.Views.Converters;

public class NodeKindToFontWeightConverter : IValueConverter
{
    public static readonly NodeKindToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeKind.Part ? FontWeight.SemiBold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class NodeKindToMarginConverter : IValueConverter
{
    public static readonly NodeKindToMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeKind.Chapter ? new Thickness(12, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class NodeKindToForegroundConverter : IValueConverter
{
    public static readonly NodeKindToForegroundConverter Instance = new();

    private static SolidColorBrush PartBrush    => ThemeBrushes.Get("Cora.Accent.Store", "#9D4EDD");
    private static SolidColorBrush ChapterBrush => ThemeBrushes.Get("Cora.Text", "#EDE8F5");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeKind.Part ? PartBrush : ChapterBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns purple for parts, grey for missing chapters, muted lavender for excluded chapters, and light for included chapters.</summary>
public class NodeKindAndMissingToForegroundConverter : IMultiValueConverter
{
    public static readonly NodeKindAndMissingToForegroundConverter Instance = new();

    // Theme brushes are stable and recoloured on a theme change, so caching them in fields is safe.
    private static SolidColorBrush PartBrush     => ThemeBrushes.Get("Cora.Accent.Store", "#9D4EDD");
    private static SolidColorBrush ChapterBrush  => ThemeBrushes.Get("Cora.Text", "#EDE8F5");
    private static SolidColorBrush MissingBrush  => ThemeBrushes.Get("Cora.Text.Disabled", "#6B7280");
    private static SolidColorBrush ExcludedBrush => ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0");

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return ChapterBrush;
        if (values[0] is NodeKind.Part) return PartBrush;
        if (values[1] is true) return MissingBrush;
        return values.Count > 2 && values[2] is true ? ExcludedBrush : ChapterBrush;
    }
}

public class NodeKindIsChapterConverter : IValueConverter
{
    public static readonly NodeKindIsChapterConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeKind.Chapter;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns true when NodeKind == Chapter, IsMissing == false, and IsExcluded == false when supplied.</summary>
public class NodeKindIsChapterAndPresentConverter : IMultiValueConverter
{
    public static readonly NodeKindIsChapterAndPresentConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        if (values[0] is not NodeKind.Chapter || values[1] is not false)
            return false;

        return values.Count < 3 || values[2] is false;
    }
}

public class NodeKindIsPartConverter : IValueConverter
{
    public static readonly NodeKindIsPartConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeKind.Part;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts bool → Cora.Accent.Store (active) or Cora.Text.Secondary (inactive).
/// Used for section-header icons in the explorer bar so the lit icon matches the expanded state.
/// </summary>
public sealed class ActiveIconBrushConverter : IValueConverter
{
    public static readonly ActiveIconBrushConverter Instance = new();

    private static IBrush ActiveBrush   => ThemeBrushes.Get("Cora.Accent.Store", "#9D4EDD");
    private static IBrush InactiveBrush => ThemeBrushes.Get("Cora.Text.Secondary", "#88919B");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts <see cref="bool"/> → <see cref="GridLength(1, Star)"/> when true,
/// <see cref="GridLength(0)"/> when false.  Used to collapse hidden right-rail sections.
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public static readonly BoolToGridLengthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}