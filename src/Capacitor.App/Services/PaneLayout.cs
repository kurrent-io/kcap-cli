namespace Capacitor.App.Services;

/// The window's side panes: the session rail on the left and the work-context pane every session
/// view shows on the right. One instance per window, so each retained session view follows it.
public sealed class PaneLayout {
    public const double RailMinWidth = 330;
    public const double ContextMinWidth = 320;

    // Of the space the pane shares, so its splitter stays on screen and can be dragged back.
    public const double MaxShare = 0.9;

    // How a pane dragged below its minimum looks until it is released and hides.
    public const double HidingOpacity = 0.4;

    public static bool HidesAt(double width, double min) => width < min;

    double _railWidth = RailMinWidth;
    bool _railCollapsed;
    double _contextWidth = ContextMinWidth;
    bool _contextCollapsed;

    public double RailWidth { get => _railWidth; set => Set(ref _railWidth, value); }
    public bool RailCollapsed { get => _railCollapsed; set => Set(ref _railCollapsed, value); }
    public double ContextWidth { get => _contextWidth; set => Set(ref _contextWidth, value); }
    public bool ContextCollapsed { get => _contextCollapsed; set => Set(ref _contextCollapsed, value); }

    public event EventHandler? Changed;

    void Set<T>(ref T field, T value) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
