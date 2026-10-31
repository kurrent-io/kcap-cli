using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Views;

/// The vendor's mark as bindable content (AgentChip's leading glyph), from a vendor token or a
/// HarnessId. One-way; parameter is the size in pixels. Bindings evaluate on the UI thread, which
/// BuildGlyph requires.
public sealed class VendorGlyphConverter : IValueConverter {
    public static readonly VendorGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is HarnessId id ? id.VendorId : value) is string vendor
            ? LaunchChipsView.BuildGlyph(vendor, double.TryParse(parameter as string, out var size) ? size : 13)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
