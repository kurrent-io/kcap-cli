using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

public sealed class VendorChipBackgroundConverter : IValueConverter {
    public static readonly VendorChipBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new ImmutableSolidColorBrush(Color.Parse(VendorChipPalette.For(value as string).Background));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
