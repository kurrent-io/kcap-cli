using System.Globalization;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views;

/// The first bound value that holds text: a preferred label, then its fallbacks in binding order.
public sealed class FirstTextConverter : IMultiValueConverter {
    public static readonly FirstTextConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.OfType<string>().FirstOrDefault(value => value.Length > 0) ?? "";
}
