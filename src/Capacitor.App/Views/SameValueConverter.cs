using System.Globalization;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views;

/// Whether two bound values are equal (Same) or not (Different), for showing one item of a list
/// differently from the rest, such as the row being renamed.
public sealed class SameValueConverter : IMultiValueConverter {
    public static readonly SameValueConverter Same = new(true);
    public static readonly SameValueConverter Different = new(false);

    readonly bool _same;

    SameValueConverter(bool same) => _same = same;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [var first, var second] && Equals(first, second) == _same;
}
