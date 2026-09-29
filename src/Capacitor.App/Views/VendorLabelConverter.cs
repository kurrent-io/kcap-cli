using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Vendor token to its display name ("claude" → "Claude"). An unknown token stays as given.
public sealed class VendorLabelConverter : IValueConverter {
    public static readonly VendorLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor ? HostedHarnessCatalog.LabelFor(vendor) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
