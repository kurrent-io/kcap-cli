using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Tone → brush for the rail's branch glyph, resolved from the application palette so the glyph
/// and the PR card's status label can never disagree about what a colour means.
public sealed class PullRequestToneBrushConverter : IValueConverter {
    public static readonly PullRequestToneBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.FindResource(value is PullRequestTone tone ? KeyFor(tone) : "KcapFaintBrush") as IBrush;

    public static string KeyFor(PullRequestTone tone) => tone switch {
        PullRequestTone.Ready => "KcapSuccessBrush",
        PullRequestTone.Draft => "KcapMutedBrush",
        PullRequestTone.ChecksRunning => "KcapMutedBrush",
        PullRequestTone.ChecksFailed => "KcapDangerBrush",
        PullRequestTone.Conflict => "KcapWarningBrush",
        PullRequestTone.Merged => "KcapMutedBrush",
        PullRequestTone.Closed => "KcapDangerBrush",
        _ => "KcapFaintBrush",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
