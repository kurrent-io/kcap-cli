using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>
/// One daemon per enrolled, signed-in user. A non-zero exit relaunches after <see cref="RelaunchPause"/>;
/// exit 0 is a deliberate stop and stays stopped until a sign-in or an explicit start. Detaching leaves
/// every daemon running, so a service restart or upgrade does not take users' agents down; the next
/// start adopts them before it launches anything
/// </summary>
public sealed class Supervisor(IDaemonHost host, TimeProvider time, IServiceLog log) {
    public static readonly TimeSpan RelaunchPause    = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GracefulStopWait = TimeSpan.FromSeconds(15);

    sealed class Entry(SessionUser user, Enrollment enrollment) {
        public SessionUser User = user;
        public Enrollment Enrollment = enrollment;
        public IRunningDaemon? Daemon;
        public bool WantRunning = true;
        public int Launches;
        public int? LastExit;
        public bool Adopted;
        public DaemonState State = DaemonState.Running;
        public string? Error;
    }

    readonly Lock _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _detached;

    public void Start() {
        foreach (var session in host.ActiveSessions()) Ensure(session, "service start", force: true);
    }

    public void OnLogon(int sessionId) => Ensure(sessionId, "logon", force: true);

    public void OnUnlock(int sessionId) => Ensure(sessionId, "unlock", force: false);

    public void OnLogoff(int sessionId) {
        lock (_gate) {
            foreach (var entry in _entries.Values.Where(e => e.User.SessionId == sessionId)) {
                entry.WantRunning = false;
                entry.State = DaemonState.SessionEnded;
                log.Write($"logoff of session {sessionId}: {entry.Enrollment.Name} is not relaunched");
            }
        }
    }

    public void Detach() => _detached = true;

    public DaemonStatus Status(string sid) {
        lock (_gate) {
            if (_entries.TryGetValue(sid, out var e)) return Describe(e);
        }
        return new DaemonStatus(DaemonState.NoSession);
    }

    /// <summary>An explicit start overrides a deliberate stop</summary>
    public DaemonStatus Start(string sid) {
        if (SessionOf(sid) is not { } user) return new DaemonStatus(DaemonState.NoSession, Error: "no signed-in session for this user");
        if (Ensure(user.SessionId, "start request", force: true) is { } refusal) {
            lock (_gate) {
                if (_entries.TryGetValue(sid, out var entry)) return Describe(entry);
            }
            return new DaemonStatus(DaemonState.NotEnrolled, Error: refusal);
        }
        return Status(sid);
    }

    public async Task<DaemonStatus> StopAsync(string sid, CancellationToken ct) {
        Entry? entry;
        IRunningDaemon? daemon;
        lock (_gate) {
            if (!_entries.TryGetValue(sid, out entry) || entry.Daemon is null) return Status(sid);
            entry.WantRunning = false;
            entry.State = DaemonState.Stopping;
            daemon = entry.Daemon;
        }

        try {
            host.RequestStop(entry.User, entry.Enrollment);
        } catch (Exception ex) {
            log.Write($"graceful stop of {entry.Enrollment.Name} could not be requested: {ex.Message}");
        }

        var finished = await Task.WhenAny(daemon.Exited, Task.Delay(GracefulStopWait, time, ct)).ConfigureAwait(false);
        if (finished != daemon.Exited) {
            log.Write($"{entry.Enrollment.Name} (pid {daemon.Pid}) did not stop within {GracefulStopWait.TotalSeconds:0}s; ending it");
            daemon.Terminate();
            await daemon.Exited.ConfigureAwait(false);
        }

        lock (_gate) {
            if (entry.Daemon is null || ReferenceEquals(entry.Daemon, daemon)) entry.State = DaemonState.Stopped;
            return Describe(entry);
        }
    }

    /// <summary>Drops a user the service no longer supervises; their daemon is already stopped</summary>
    public void Forget(string sid) {
        lock (_gate) _entries.Remove(sid);
    }

    SessionUser? SessionOf(string sid) {
        foreach (var session in host.ActiveSessions()) {
            if (host.UserOf(session) is { } user && string.Equals(user.Sid, sid, StringComparison.OrdinalIgnoreCase)) return user;
        }
        return null;
    }

    /// <summary>Null when the session's user has a daemon (now or already); otherwise why not</summary>
    string? Ensure(int sessionId, string why, bool force) {
        SessionUser? user;
        Enrollment? enrollment;
        string? refusal;
        try {
            user = host.UserOf(sessionId);
            if (user is null) return "no user in session";
            enrollment = host.EnrollmentOf(user, out refusal);
        } catch (Exception ex) {
            log.Write($"{why}: session {sessionId}: {ex.Message}");
            return ex.Message;
        }

        if (enrollment is null) {
            log.Write($"{why}: session {sessionId} user {user.Sid} is not enrolled{(refusal is null ? "" : $": {refusal}")}");
            return refusal ?? "not enrolled";
        }

        IRunningDaemon daemon;
        lock (_gate) {
            _entries.TryGetValue(user.Sid, out var entry);
            if (entry?.Daemon is not null) return null;
            if (entry is { WantRunning: false } && !force) return null;

            entry ??= new Entry(user, enrollment);
            entry.User = user;
            entry.Enrollment = enrollment;
            entry.WantRunning = true;
            _entries[user.Sid] = entry;

            try {
                var adopted = host.FindRunning(user, enrollment);
                daemon = adopted ?? host.Launch(user, enrollment);
                entry.Adopted = adopted is not null;
                if (adopted is null) entry.Launches++;
                entry.Daemon = daemon;
                entry.State = DaemonState.Running;
                entry.Error = null;
                log.Write($"{why}: {(adopted is null ? "launched" : "adopted")} {enrollment.Name} pid {daemon.Pid} in session {sessionId}{(daemon.HasJob ? "" : " without its job")}");
            } catch (Exception ex) {
                entry.State = DaemonState.Failed;
                entry.Error = ex.Message;
                log.Write($"{why}: launching {enrollment.Name} in session {sessionId} failed: {ex.Message}");
                return ex.Message;
            }
        }

        _ = WatchAsync(user.Sid, daemon);
        return null;
    }

    async Task WatchAsync(string sid, IRunningDaemon daemon) {
        var code = await daemon.Exited.ConfigureAwait(false);
        daemon.Dispose();
        if (_detached) return;

        int session;
        bool relaunch;
        lock (_gate) {
            if (!_entries.TryGetValue(sid, out var e) || !ReferenceEquals(e.Daemon, daemon)) return;
            e.Daemon = null;
            e.LastExit = code;
            relaunch = e.WantRunning && code != 0;
            e.State = relaunch ? DaemonState.Relaunching
                : e.State is DaemonState.SessionEnded ? DaemonState.SessionEnded
                : DaemonState.Stopped;
            session = e.User.SessionId;
            log.Write($"{e.Enrollment.Name} pid {daemon.Pid} exited with {code}{(relaunch ? $"; relaunching in {RelaunchPause.TotalSeconds:0}s" : "")}");
        }
        if (!relaunch) return;

        await Task.Delay(RelaunchPause, time).ConfigureAwait(false);
        lock (_gate) {
            if (_detached || !_entries.TryGetValue(sid, out var e) || !e.WantRunning) return;
        }
        Ensure(session, "relaunch", force: false);
    }

    static DaemonStatus Describe(Entry e) => new(
        e.State, e.Enrollment.Name, e.Daemon?.Pid, e.User.SessionId, e.Launches, e.LastExit, e.Adopted,
        e.Daemon?.HasJob ?? false, e.Error);
}
