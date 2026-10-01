using System.Globalization;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views.Onboarding;

/// Button labels in the setup register are capitals; the view models keep sentence case.
public sealed class UpperCase : IValueConverter {
    public static readonly UpperCase Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(CultureInfo.CurrentCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
