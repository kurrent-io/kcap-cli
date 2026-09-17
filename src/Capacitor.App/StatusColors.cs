namespace Capacitor.App;

/// Hex-only constants (plain strings, not Brush instances) shared by MainWindowViewModel's
/// status word and TrayIconRenderer's per-state tray-icon overlay, so the window and the
/// tray icon can never disagree about what a color means. Callers build their own Brush per
/// use (see MainWindowViewModel.Paint) rather than caching one here, for the same
/// UI-thread-affinity reason documented there.
public static class StatusColors {
    public const string Connected   = "#4CAF50";
    public const string InProgress  = "#FFB300";
    public const string Disrupted   = "#E53935";
    public const string Unavailable = "#9E9E9E";
    /// Live process whose turn is idle / waiting on the user. Same paint as KcapWarning —
    /// Connected would read as running or settled.
    public const string Waiting     = "#F4B860";
}
