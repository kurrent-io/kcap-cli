using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

/// <param name="sessions">Shared with another host, so the daemon and server hosts see the same sign-ins</param>
class FakeHost<TEnrollment>(Func<string, TEnrollment> enrollmentFor, Dictionary<int, SessionUser>? sessions = null)
    : IDaemonHost<TEnrollment> where TEnrollment : class, ISupervisedEnrollment {
    readonly Lock _gate = new();
    int _nextPid = 1000;

    public readonly Dictionary<int, SessionUser> Sessions = sessions ?? [];
    public readonly Dictionary<string, TEnrollment> Enrollments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enrollments that exist but launch nothing, with the refusal, as a stopped server's does</summary>
    public readonly Dictionary<string, string> Refusals = new(StringComparer.OrdinalIgnoreCase);

    public readonly List<FakeRunningDaemon> Launched = [];
    public readonly Dictionary<string, FakeRunningDaemon> ByUser = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> StopRequests = [];
    public readonly List<int> StopPids = [];
    public FakeRunningDaemon? Running;
    public bool StopRequestEndsDaemon;
    public bool LaunchFails;
    public Action<SessionUser>? BeforeLaunch;

    public SessionUser SignIn(int sessionId, string sid) {
        var user = new SessionUser(sessionId, sid, $@"C:\Users\{sid}");
        Sessions[sessionId] = user;
        Enroll(sid);
        return user;
    }

    public void Enroll(string sid) => Enrollments[sid] = enrollmentFor(sid);

    public IReadOnlyList<int> ActiveSessions() => [.. Sessions.Keys];

    public SessionUser? UserOf(int sessionId) => Sessions.GetValueOrDefault(sessionId);

    public TEnrollment? EnrollmentOf(SessionUser user, out string? refusal) {
        if (Refusals.TryGetValue(user.Sid, out refusal)) return null;
        refusal = Enrollments.ContainsKey(user.Sid) ? null : "no enrollment";
        return Enrollments.GetValueOrDefault(user.Sid);
    }

    public bool HasEnrollment(SessionUser user) => Enrollments.ContainsKey(user.Sid) || Refusals.ContainsKey(user.Sid);

    public IRunningDaemon? FindRunning(SessionUser user, TEnrollment enrollment) {
        var running = Running;
        Running = null;
        return running;
    }

    public IRunningDaemon Launch(SessionUser user, TEnrollment enrollment) {
        BeforeLaunch?.Invoke(user);
        if (LaunchFails) throw new InvalidOperationException("CreateProcessAsUser failed");
        lock (_gate) {
            var daemon = new FakeRunningDaemon(_nextPid++);
            Launched.Add(daemon);
            ByUser[user.Sid] = daemon;
            return daemon;
        }
    }

    public void RequestStop(SessionUser user, TEnrollment enrollment, int pid) {
        StopRequests.Add(user.Sid);
        StopPids.Add(pid);
        if (StopRequestEndsDaemon && ByUser.TryGetValue(user.Sid, out var daemon)) daemon.Exit(0);
    }

    public int LaunchCount {
        get { lock (_gate) return Launched.Count; }
    }
}
