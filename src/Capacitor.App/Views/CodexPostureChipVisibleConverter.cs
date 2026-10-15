using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

public sealed class CodexPostureChipVisibleConverter : IValueConverter {
    public static readonly CodexPostureChipVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor && HostedHarnessCatalog.SupportsCodexPosture(vendor);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
