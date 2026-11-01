using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>Everything the supervisor asks of Windows about one kind of per-user process. The production hosts
/// bind WTS, userenv and CreateProcessAsUser; tests substitute a fake</summary>
public interface IDaemonHost<TEnrollment> where TEnrollment : class, ISupervisedEnrollment {
    IReadOnlyList<int> ActiveSessions();

    /// <summary>Null when no user is signed in to the session</summary>
    SessionUser? UserOf(int sessionId);

    /// <summary>Null when the user has not enrolled, their enrollment cannot describe a process, or it holds
    /// the process down</summary>
    TEnrollment? EnrollmentOf(SessionUser user, out string? refusal);

    /// <summary>Whether the user has an enrollment at all, valid or not and wanted or not</summary>
    bool HasEnrollment(SessionUser user);

    /// <summary>A process already running from the enrolled binary in the user's session, which a previous
    /// run of the service started</summary>
    IRunningDaemon? FindRunning(SessionUser user, TEnrollment enrollment);

    IRunningDaemon Launch(SessionUser user, TEnrollment enrollment);

    /// <summary>Asks the process to stop itself, running the user's own CLI as the user. Returns without
    /// waiting; the caller watches the exit</summary>
    void RequestStop(SessionUser user, TEnrollment enrollment, int pid);
}
