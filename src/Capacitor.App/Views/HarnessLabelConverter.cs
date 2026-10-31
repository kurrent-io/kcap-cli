using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Views;

/// A harness id as the product name people know it by ("Claude Code", "Codex").
public sealed class HarnessLabelConverter : IValueConverter {
    public static readonly HarnessLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HarnessId id ? HarnessRegistry.LabelOf(id) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
