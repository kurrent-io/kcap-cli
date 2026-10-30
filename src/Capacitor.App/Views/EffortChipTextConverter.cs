using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// EffortChip's label: always "Effort · …" — Default when null, otherwise the chosen rung's
/// sentence-case label. Keeps the category visible after a pick.
public sealed class EffortChipTextConverter : IValueConverter {
    public static readonly EffortChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"Effort · {HostedHarnessCatalog.EffortLabelFor(value as string)}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
