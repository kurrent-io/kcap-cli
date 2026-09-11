using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// The vendor chip's fill brush, from the vendor string via VendorChipPalette. The lookup lives in
/// the view layer (not the row VM) so the VM carries no Avalonia brushes; the brush is immutable, so
/// it is safe to share across the rows that bind it.
public sealed class VendorChipBackgroundConverter : IValueConverter {
    public static readonly VendorChipBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new ImmutableSolidColorBrush(Color.Parse(VendorChipPalette.For(value as string).Background));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
