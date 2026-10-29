using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>Everything the supervisor asks of Windows. The production host binds WTS, userenv and
/// CreateProcessAsUser; tests substitute a fake</summary>
public interface IDaemonHost {
    IReadOnlyList<int> ActiveSessions();

    /// <summary>Null when no user is signed in to the session</summary>
    SessionUser? UserOf(int sessionId);

    /// <summary>Null when the user has not enrolled, or their enrollment cannot describe a daemon</summary>
    Enrollment? EnrollmentOf(SessionUser user, out string? refusal);

    /// <summary>A daemon already running from the enrolled binary in the user's session, which a previous
    /// run of the service started</summary>
    IRunningDaemon? FindRunning(SessionUser user, Enrollment enrollment);

    IRunningDaemon Launch(SessionUser user, Enrollment enrollment);

    /// <summary>Asks the daemon to stop itself, running the user's own CLI as the user. Returns without
    /// waiting; the caller watches the daemon's exit</summary>
    void RequestStop(SessionUser user, Enrollment enrollment);
}
