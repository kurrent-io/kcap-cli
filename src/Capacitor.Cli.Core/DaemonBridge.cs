namespace Capacitor.Cli.Core;

/// <summary>
/// The endpoint a daemon-hosted agent posts permission traffic to, as
/// <see cref="HostedAgent.BridgeUrlVar"/> named it. A hook payload carries the tool name and the
/// raw tool input, and the variable carries no auth, so anything but an http loopback address is
/// refused rather than posted to.
/// </summary>
/// <remarks>
/// The three cases are distinct because callers answer them differently: a bridge that was named
/// and refused is a misconfiguration, and refusing the traffic is not the same decision as refusing
/// the agent's request.
/// </remarks>
public abstract record DaemonBridge {
    DaemonBridge() { }

    /// <summary>No bridge named — a terminal session the user started themselves. Only an unset or
    /// empty variable: a value made of blanks was set by someone, and reporting it is what tells
    /// them why their bridge is being ignored.</summary>
    public static readonly DaemonBridge None = new NotNamed();

    public sealed record NotNamed : DaemonBridge;

    /// <summary><paramref name="Value"/> is the offending address, for the caller to report.</summary>
    public sealed record NotLoopback(string Value) : DaemonBridge;

    /// <summary>Trailing slash stripped; callers append their own route.</summary>
    public sealed record Loopback(string BaseUrl) : DaemonBridge;

    /// <summary>
    /// The literal <c>127.0.0.1</c> only: <c>localhost</c> resolves to whatever the host's
    /// resolver says, which a misconfigured machine can point off-loopback, and https implies an
    /// endpoint this variable does not name.
    /// </summary>
    public static DaemonBridge Parse(string? daemonUrl) {
        if (string.IsNullOrEmpty(daemonUrl)) return None;

        return Uri.TryCreate(daemonUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http"
            && uri.Host is "127.0.0.1"
                ? new Loopback(daemonUrl.TrimEnd('/'))
                : new NotLoopback(daemonUrl);
    }
}
