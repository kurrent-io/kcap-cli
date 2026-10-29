using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The boot reaper has already proven every predecessor agent process gone, so a session
/// the resume lane continues is exited by construction.</summary>
internal sealed class ProvenExitedLiveness : ISessionLivenessSource {
    public void Reap() { }

    public SessionLiveness Liveness(SessionId session) => SessionLiveness.Exited;
}
