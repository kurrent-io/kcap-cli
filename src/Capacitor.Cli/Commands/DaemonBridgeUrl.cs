namespace Capacitor.Cli.Commands;

/// <summary>
/// Loopback validation for <c>KAPACITOR_DAEMON_URL</c>. Both the Claude and
/// Codex permission-request hook CLI commands must refuse to POST permission
/// payloads to anything other than an HTTP loopback URL — non-loopback or
/// HTTPS values usually indicate a misconfigured environment variable, and
/// we don't want hook payloads leaving the loopback interface.
/// </summary>
public static class DaemonBridgeUrl {
    /// <summary>
    /// True when <paramref name="daemonUrl"/> is a valid <c>http://127.0.0.1:.../...</c>
    /// URL. Returns the parsed-and-trailing-slash-stripped form via
    /// <paramref name="baseUrl"/> (callers append <c>/{vendor}/permission-request</c>
    /// themselves).
    /// </summary>
    public static bool TryParseLoopback(string? daemonUrl, out string baseUrl) {
        baseUrl = "";

        if (string.IsNullOrWhiteSpace(daemonUrl)) return false;
        if (!Uri.TryCreate(daemonUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http") return false;
        if (uri.Host != "127.0.0.1") return false;

        baseUrl = daemonUrl.TrimEnd('/');
        return true;
    }
}
