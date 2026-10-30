using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// PermissionChip's label: always "Permissions · …" so a bare "Manual" is not mistaken for an
/// effort or harness setting.
public sealed class PermissionChipTextConverter : IValueConverter {
    public static readonly PermissionChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string token
            ? $"Permissions · {HostedHarnessCatalog.PermissionModeLabelFor(token)}"
            : "Permissions · Manual";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
