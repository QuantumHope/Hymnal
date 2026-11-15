using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Hymnal.Views.Converters;

/// <summary>True → a left indent, for a file inside a part or section in the Details list.</summary>
public sealed class BoolToIndentConverter : IValueConverter
{
    public static readonly BoolToIndentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(16, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True → bold, for a divider row in the Details list.</summary>
public sealed class BoolToBoldConverter : IValueConverter
{
    public static readonly BoolToBoldConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
