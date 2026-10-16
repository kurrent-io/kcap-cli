using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App;

public static class StatusColors {
    public static string Connected => ColorFor("KcapSuccessBrush");
    public static string InProgress => ColorFor("KcapPurpleBrush");
    public static string Disrupted => ColorFor("KcapDangerBrush");
    public static string Unavailable => ColorFor("KcapFaintBrush");

    static string ColorFor(string key) =>
        ((ISolidColorBrush)Application.Current!.FindResource(key)!).Color.ToString();
}
