using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Tone → brush for a usage bar, the one mapping every usage surface draws with. The parameter
/// "Text" picks the percentage's colour, which only warning and danger tint.
public sealed class UsageToneBrushConverter : IValueConverter {
    public static readonly UsageToneBrushConverter Instance = new();

    public static string FillKey(UsageTone tone) => tone switch {
        UsageTone.Warning => "KcapWarningBrush",
        UsageTone.Danger  => "KcapDangerBrush",
        UsageTone.Unknown => "KcapBorderStrongBrush",
        _                 => "KcapMutedBrush",
    };

    public static string TextKey(UsageTone tone) => tone is UsageTone.Warning or UsageTone.Danger ? FillKey(tone) : "KcapMutedBrush";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var tone = value as UsageTone? ?? UsageTone.Unknown;
        return Application.Current?.FindResource(parameter as string == "Text" ? TextKey(tone) : FillKey(tone)) as IBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
