namespace Capacitor.App;

/// Status hex values shared by every status surface so they cannot disagree. Plain strings: a
/// caller that caches must use an immutable brush.
public static class StatusColors {
    public const string Connected   = "#4CAF50";
    public const string InProgress  = "#FFB300";
    public const string Disrupted   = "#E53935";
    public const string Unavailable = "#9E9E9E";
    /// Live process whose turn is idle / waiting on the user. Same paint as KcapWarning —
    /// Connected would read as running or settled.
    public const string Waiting     = "#F4B860";
}
