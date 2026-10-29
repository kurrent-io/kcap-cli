namespace Capacitor.App;

/// Status hex values shared by every status surface so they cannot disagree. Plain strings: a
/// caller that caches must use an immutable brush.
public static class StatusColors {
    public const string Connected   = "#5BE0B3";
    public const string InProgress  = "#F4B860";
    public const string Disrupted   = "#FF7272";
    public const string Unavailable = "#949BAA";
    /// Work in flight, the same purple as KcapPurpleBrush: an update still finishing is neither attention nor an outage.
    public const string Updating    = "#A994FF";
}
