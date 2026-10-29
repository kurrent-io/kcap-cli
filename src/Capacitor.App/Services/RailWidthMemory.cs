using Avalonia.Controls;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

public static class RailWidthMemory {
    public const double DefaultWidth = 330;

    // Of the window, so the splitter stays on screen and the workspace beside it can be dragged back.
    public const double MaxShare = 0.9;

    public static GridLength DefaultWidthGridLength { get; } = new(DefaultWidth);

    public static double Resolve(double? saved) =>
        saved is double value && double.IsFinite(value) && value >= 0 ? value : DefaultWidth;

    public static void Apply(MainWindow window, AppState state) => window.RailWidth = Resolve(state.RailWidth);

    public static void Attach(MainWindow window, IAppStateStore store) =>
        window.RailResized += (_, _) => {
            var width = window.RailWidth;
            _ = store.UpdateAsync(s => s with { RailWidth = width });
        };
}
