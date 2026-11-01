using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>service status</c> adds under kcap-service: how the service holds this daemon, and whether it runs
/// the local server too</summary>
static class ServiceSupervisionLines {
    /// <param name="daemon">The service's account of the daemon; null when it is not enrolled under this name</param>
    /// <param name="serverStopped">The local server enrollment's deliberate stop; null when the service runs no local
    /// server for this user</param>
    public static IEnumerable<string> For(string id, ServicePipeStatus? daemon, ServicePipeStatus? server, bool? serverStopped) {
        if (daemon is not null) yield return $"  Supervisor: {Daemon(id, daemon)}";
        if (serverStopped is { } stopped) yield return $"  Local server: {Server(server, stopped)}";
    }

    static string Daemon(string id, ServicePipeStatus s) => s.State switch {
        "running" when s.Adopted =>
            $"kcap-service adopted this daemon (pid {s.Pid}), already running when the service started, and restarts it if it crashes."
          + (s.HasJob ? "" : " It is outside the service's job, so a stop it does not answer ends the daemon but not its agents."),
        "running"     => $"kcap-service started this daemon (pid {s.Pid}) and restarts it if it crashes.",
        "relaunching" => $"kcap-service restarts this daemon in a few seconds; it exited with {s.LastExit}.",
        "stopping"    => "kcap-service is stopping this daemon.",
        "stopped"     => $"kcap-service keeps this daemon stopped until `kcap daemon service start --name {id}`.",
        "failed"      => $"kcap-service could not start this daemon: {s.Error}",
        _             => $"kcap-service reports this daemon as {s.State.Replace('_', ' ')}.",
    };

    static string Server(ServicePipeStatus? s, bool stopped) =>
        stopped ? "kcap-service runs it too; it stays stopped until `kcap local start`."
        : s is null ? "enrolled with kcap-service, which did not report on it."
        : s.State switch {
            "running"     => $"kcap-service runs it too (pid {s.Pid}{(s.Adopted ? ", adopted when the service started" : "")}).",
            "relaunching" => "kcap-service runs it too, and restarts it in a few seconds.",
            "failed"      => $"kcap-service runs it too, but could not start it: {s.Error}",
            _             => $"kcap-service runs it too; it is {s.State.Replace('_', ' ')}.",
        };
}
