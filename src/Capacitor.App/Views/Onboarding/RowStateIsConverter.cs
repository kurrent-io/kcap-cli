using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Views.Onboarding;

/// True when the bound <see cref="RowState"/> is the one named by the converter parameter.
public sealed class RowStateIsConverter : IValueConverter {
    public static readonly RowStateIsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RowState state && parameter is string name && Enum.TryParse<RowState>(name, out var wanted) && state == wanted;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
