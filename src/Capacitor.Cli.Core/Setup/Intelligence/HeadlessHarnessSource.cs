using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>Which harnesses to offer as the local agent: what a connected daemon advertises, since that is the list the
/// server sees, else <see cref="HeadlessHarnessProbe"/>'s answer for this machine.</summary>
public static class HeadlessHarnessSource {
    static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(2);

    public static string[] Choose(ProbeResult daemon, Func<string[]> probe) =>
        daemon is { Hello.Capabilities: { } capabilities, Snapshot.Daemon: { Connection: "connected", HeadlessVendors: { } listed } }
     && capabilities.Contains(HeadlessStatus.Capability)
            ? listed
            : probe();

    /// <summary>Never throws short of cancellation: a daemon that cannot be dialled, even for a socket path the platform
    /// refuses, is one not connected.</summary>
    public static async Task<string[]> ReadAsync(
            DaemonStore store, string daemonName, TimeProvider time, Func<string[]> probe, CancellationToken ct) {
        ProbeResult daemon;

        try {
            daemon = await LocalControlProbe.ProbeAsync(store, daemonName, time, ProbeBudget, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            daemon = new ProbeResult(false, null, null, false);
        }

        return Choose(daemon, probe);
    }
}
