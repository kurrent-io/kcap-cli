namespace Capacitor.App.Services;

public static class PaneLayoutMemory {
    public static double Resolve(double? saved, double min) =>
        saved is double value && double.IsFinite(value) ? Math.Max(min, value) : min;

    public static void Apply(PaneLayout panes, AppState state) {
        panes.RailWidth = Resolve(state.RailWidth, PaneLayout.RailMinWidth);
        panes.RailCollapsed = state.RailCollapsed;
        panes.ContextWidth = Resolve(state.ContextPaneWidth, PaneLayout.ContextMinWidth);
        panes.ContextCollapsed = state.ContextPaneCollapsed;
    }

    public static void Attach(PaneLayout panes, IAppStateStore store) =>
        panes.Changed += (_, _) => {
            var (railWidth, railCollapsed) = (panes.RailWidth, panes.RailCollapsed);
            var (contextWidth, contextCollapsed) = (panes.ContextWidth, panes.ContextCollapsed);
            _ = store.UpdateAsync(s => s with {
                RailWidth = railWidth,
                RailCollapsed = railCollapsed,
                ContextPaneWidth = contextWidth,
                ContextPaneCollapsed = contextCollapsed,
            });
        };
}
