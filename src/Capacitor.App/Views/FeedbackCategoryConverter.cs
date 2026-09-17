using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.App.Views;

/// <summary>Binds one radio chip to one enum value: checked when the bound category equals the
/// parameter, and writes the parameter back when the chip is checked.</summary>
public sealed class FeedbackCategoryConverter : IValueConverter {
    public static readonly FeedbackCategoryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FeedbackCategory current && parameter is FeedbackCategory wanted && current == wanted;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is FeedbackCategory wanted ? wanted : BindingOperations.DoNothing;
}
