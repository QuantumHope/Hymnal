using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.Theming;

namespace Hymnal.Views.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    public static readonly StatusToBrushConverter Instance = new();

    // ThemeBrushes hands out one brush per key and recolours it on a theme change, so bound dots follow the theme.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChapterStatus status && KeyFor(status) is { } k
            ? ThemeBrushes.Get(k.Key, k.Fallback)
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>The theme key for a status colour (see <see cref="StatusColors"/>), shared by the drawn charts.</summary>
    internal static (string Key, string Fallback)? KeyFor(ChapterStatus status) => StatusColors.KeyFor(status);
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.35 : 1.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolNotConverter : IValueConverter
{
    public static readonly BoolNotConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToFontStyleConverter : IValueConverter
{
    public static readonly BoolToFontStyleConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontStyle.Italic : FontStyle.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusEqualsConverter : IValueConverter
{
    public static readonly StatusEqualsConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChapterStatus status && parameter is ChapterStatus expected && status == expected;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="ProjectProfile"/> to the profile's label for the
/// <see cref="ChapterStatus"/> passed as the converter parameter.
/// </summary>
public sealed class ProfileStatusLabelConverter : IValueConverter
{
    public static readonly ProfileStatusLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => parameter is ChapterStatus status
            ? (value as ProjectProfile ?? ProjectProfile.Novel).LabelFor(status)
            : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the <see cref="ChapterStatus"/> passed as the converter parameter is one of the
/// bound <see cref="ProjectProfile"/>'s visible statuses.
/// </summary>
public sealed class ProfileShowsStatusConverter : IValueConverter
{
    public static readonly ProfileShowsStatusConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => parameter is ChapterStatus status
           && (value as ProjectProfile ?? ProjectProfile.Novel).VisibleStatuses.Any(o => o.Value == status);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusDotTooltipConverter : IMultiValueConverter
{
    public static readonly StatusDotTooltipConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return null;

        if (values[0] is bool isMissing && isMissing)
            return "File not found";

        return values[1] is ChapterStatus status ? $"Change status: {status}" : null;
    }
}

/// <summary>
/// Converts <see cref="int?"/> ↔ <see cref="string"/> for the Set Target flyout TextBoxes.
/// ConvertBack returns null for empty or non-integer input.
/// </summary>
public sealed class NullableIntToStringConverter : IValueConverter
{
    public static readonly NullableIntToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return (int?)parsed;
        return (int?)null;
    }
}

/// <summary>
/// Converts <see cref="double?"/> ↔ <see cref="string"/> for phase progress TextBoxes.
/// ConvertBack returns null for empty input; clamps to 0–100 range.
/// </summary>
public sealed class NullableDoubleToStringConverter : IValueConverter
{
    public static readonly NullableDoubleToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d.ToString("G", CultureInfo.InvariantCulture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return (double?)Math.Clamp(parsed, 0.0, 100.0);
        return (double?)null;
    }
}
