namespace Capacitor.App.ViewModels;

/// <summary>The line appended to a desktop report so support can see which client and daemon it
/// came from. The daemon's last observed version wins: a daemon that crashed after being observed
/// reports the version the bug is about.</summary>
public static class FeedbackTrailer {
    public static string Build(string appVersion, string daemonName, string? snapshotVersion, string? cliVersion) {
        var version = snapshotVersion is { Length: > 0 } observed ? observed
            : cliVersion is { Length: > 0 } installed ? $"cli {installed}"
            : "version unknown";

        return $"Sent from Kurrent Capacitor Desktop {appVersion} · daemon {daemonName} {version}";
    }
}
