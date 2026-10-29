using System.Globalization;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views;

public sealed class CountIsNotZeroConverter : IValueConverter {
    public static readonly CountIsNotZeroConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n != 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
