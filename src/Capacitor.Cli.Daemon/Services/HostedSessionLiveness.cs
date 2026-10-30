using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A session one of this daemon's live agents runs is running here; any other is unknown, which
/// leaves the verdict to the server.</summary>
internal sealed class HostedSessionLiveness(Func<SessionId, bool> hosts) : ISessionLivenessSource {
    public void Reap() { }

    public SessionLiveness Liveness(SessionId session) => hosts(session) ? SessionLiveness.Running : SessionLiveness.Unknown;
}
