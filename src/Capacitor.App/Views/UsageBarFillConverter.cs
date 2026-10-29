using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A usage bar's fill width within a track whose width is the parameter. A window whose usage is
/// unknown fills the whole track in the unknown tone, so it never reads as zero.
public sealed class UsageBarFillConverter : IValueConverter {
    public static readonly UsageBarFillConverter Instance = new();

    public static double Width(UsageBar bar, double track) =>
        bar.Percent is { } percent ? track * Math.Clamp(percent, 0, 100) / 100.0 : track;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var track = parameter switch {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        return value is UsageBar bar ? Width(bar, track) : 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
