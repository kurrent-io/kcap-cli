using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

sealed class FakeDaemonHost : IDaemonHost {
    readonly Lock _gate = new();
    int _nextPid = 1000;

    public readonly Dictionary<int, SessionUser> Sessions = [];
    public readonly Dictionary<string, Enrollment> Enrollments = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<FakeRunningDaemon> Launched = [];
    public readonly Dictionary<string, FakeRunningDaemon> ByUser = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> StopRequests = [];
    public FakeRunningDaemon? Running;
    public bool StopRequestEndsDaemon;
    public bool LaunchFails;
    public Action<SessionUser>? BeforeLaunch;

    public SessionUser SignIn(int sessionId, string sid, string name = "laptop") {
        var user = new SessionUser(sessionId, sid, $@"C:\Users\{sid}");
        Sessions[sessionId] = user;
        Enrollments[sid] = new Enrollment(name, $@"C:\Users\{sid}\AppData\Local\kcap\current\kcap-daemon.exe", null, null);
        return user;
    }

    public IReadOnlyList<int> ActiveSessions() => [.. Sessions.Keys];

    public SessionUser? UserOf(int sessionId) => Sessions.GetValueOrDefault(sessionId);

    public Enrollment? EnrollmentOf(SessionUser user, out string? refusal) {
        refusal = Enrollments.ContainsKey(user.Sid) ? null : "no enrollment";
        return Enrollments.GetValueOrDefault(user.Sid);
    }

    public IRunningDaemon? FindRunning(SessionUser user, Enrollment enrollment) {
        var running = Running;
        Running = null;
        return running;
    }

    public IRunningDaemon Launch(SessionUser user, Enrollment enrollment) {
        BeforeLaunch?.Invoke(user);
        if (LaunchFails) throw new InvalidOperationException("CreateProcessAsUser failed");
        lock (_gate) {
            var daemon = new FakeRunningDaemon(_nextPid++);
            Launched.Add(daemon);
            ByUser[user.Sid] = daemon;
            return daemon;
        }
    }

    public void RequestStop(SessionUser user, Enrollment enrollment) {
        StopRequests.Add(user.Sid);
        if (StopRequestEndsDaemon && ByUser.TryGetValue(user.Sid, out var daemon)) daemon.Exit(0);
    }

    public int LaunchCount {
        get { lock (_gate) return Launched.Count; }
    }
}
