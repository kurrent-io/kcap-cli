using System.Diagnostics.Metrics;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Minimal ACP hosted-agent counters, incremented alongside the Info-level lifecycle logs. No
/// exporter is wired — these are observable via <c>dotnet-counters monitor -p &lt;pid&gt;
/// Capacitor.Cli.Daemon.Acp</c> for local troubleshooting. Deliberately does NOT cover reconnect
/// (no reconnect path exists yet) and stays process-wide/static rather than DI-injected: there is
/// exactly one of these per daemon process and no test needs to substitute a fake meter.
/// </summary>
internal static class AcpMetrics {
    static readonly Meter Meter = new("Capacitor.Cli.Daemon.Acp");

    public static readonly Counter<long> Launches        = Meter.CreateCounter<long>("acp.launches");
    public static readonly Counter<long> SessionsStarted = Meter.CreateCounter<long>("acp.sessions_started");
    public static readonly Counter<long> BlockingRequests = Meter.CreateCounter<long>("acp.blocking_requests");
    public static readonly Counter<long> Failures         = Meter.CreateCounter<long>("acp.failures");

    /// <summary><paramref name="kind"/> is <c>"permission"</c> or <c>"elicitation"</c> — mirrors <see cref="Daemon.Acp.AcpInteractionBridge"/>'s own kind vocabulary.</summary>
    public static void RecordBlockingRequest(string kind) =>
        BlockingRequests.Add(1, new KeyValuePair<string, object?>("kind", kind));

    /// <summary><paramref name="stage"/> is a short token, e.g. <c>"handshake"</c>.</summary>
    public static void RecordFailure(string stage) =>
        Failures.Add(1, new KeyValuePair<string, object?>("stage", stage));
}
