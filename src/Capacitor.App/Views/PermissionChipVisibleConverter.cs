using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

public sealed class PermissionChipVisibleConverter : IValueConverter {
    public static readonly PermissionChipVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor && HostedHarnessCatalog.SupportsPermissionMode(vendor);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
