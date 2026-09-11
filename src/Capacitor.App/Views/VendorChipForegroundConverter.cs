using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// The vendor chip's text brush, paired with VendorChipBackgroundConverter for contrast on the fill.
public sealed class VendorChipForegroundConverter : IValueConverter {
    public static readonly VendorChipForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new ImmutableSolidColorBrush(Color.Parse(VendorChipPalette.For(value as string).Foreground));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
