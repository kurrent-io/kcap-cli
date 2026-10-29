using Avalonia.Controls;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

public static class RailWidthMemory {
    public const double DefaultWidth = 330;

    // Leaves the workspace most of a minimum-width window.
    public const double MaxWidth = 600;

    public static GridLength DefaultWidthGridLength { get; } = new(DefaultWidth);

    public static double Resolve(double? saved) =>
        saved is double value && double.IsFinite(value) && value >= 0 ? Math.Min(value, MaxWidth) : DefaultWidth;

    public static void Apply(MainWindow window, AppState state) => window.RailWidth = Resolve(state.RailWidth);

    public static void Attach(MainWindow window, IAppStateStore store) =>
        window.RailResized += (_, _) => {
            var width = window.RailWidth;
            _ = store.UpdateAsync(s => s with { RailWidth = width });
        };
}
