using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Whether this process runs under an agent the named daemon hosts. Replacing that daemon's service
/// stops the daemon, and the supervisor ends what runs under it, this command included, partway through:
/// launchd then restarts the old job and the replacement never happens.
/// </summary>
static class HostedCaller {
    /// <summary>
    /// Any one of: the daemon-id marker the daemon writes into the agents it starts, the lookup markers it
    /// writes into an ACP agent that carries no id marker, or the running daemon as an ancestor of this
    /// process. The markers cover Windows, which the ancestry walk does not; an ancestor covers a private
    /// agent, which carries no lookup markers. <c>KCAP_DAEMON_NAME</c> alone is not enough: a user may
    /// export it to pick a daemon.
    /// </summary>
    public static bool HostedBy(string serviceId, Func<string, string?> getEnv, int? daemonPid, Func<int, bool> descendsFrom) =>
        getEnv(HostedAgent.DaemonIdVar) is { Length: > 0 } marker && marker == HostedAgent.DaemonId(serviceId)
     || getEnv(HostedAgent.DaemonAgentIdVar) is { Length: > 0 }
        && getEnv(DaemonNameResolver.NameVar) is { Length: > 0 } name && DaemonStore.Sanitize(name) == serviceId
     || daemonPid is { } pid && descendsFrom(pid);

    public static bool HostedBy(DaemonStore store, string serviceId) =>
        HostedBy(serviceId, Environment.GetEnvironmentVariable, DaemonPidProbe.ValidatedPid(store, serviceId),
            ancestor => ProcessTree.Descends(Environment.ProcessId, ancestor));

    /// <summary>The refusal, naming the command to run instead and where to run it.</summary>
    public static string Refusal(string serviceId, string command) =>
        $"This command runs inside an agent that daemon '{serviceId}' hosts. Replacing that daemon's service stops the daemon "
      + "and ends this command with it, so nothing was changed. Run it from a terminal the daemon does not host, "
      + $"or use the kcap app: {command}";
}
