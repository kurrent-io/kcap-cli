namespace Capacitor.Cli.Core.Continuation;

/// <summary>What this machine knows about whether a session's agent process still runs.</summary>
public interface ISessionLivenessSource {
    void Reap();

    SessionLiveness Liveness(SessionId session);
}
