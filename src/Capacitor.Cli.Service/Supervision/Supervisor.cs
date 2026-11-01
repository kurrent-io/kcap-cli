using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>
/// One process per enrolled, signed-in user — a daemon, or a local edition server, as the host decides. A
/// non-zero exit relaunches after <see cref="RelaunchPause"/>; exit 0 is a deliberate stop and stays stopped
/// until a sign-in or an explicit start. Detaching leaves every process running, so a service restart or
/// upgrade does not take users' work down; the next start adopts them before it launches anything
/// </summary>
/// <param name="gracefulStopWait">How long a stop waits on the process's own shutdown before it ends the job</param>
/// <param name="supervising">Told each user whose process the supervisor launched or adopted</param>
public sealed class Supervisor<TEnrollment>(
        IDaemonHost<TEnrollment> host, TimeProvider time, IServiceLog log, TimeSpan gracefulStopWait,
        Action<string>? supervising = null)
    where TEnrollment : class, ISupervisedEnrollment {
    public static readonly TimeSpan RelaunchPause = TimeSpan.FromSeconds(5);

    sealed class Entry(SessionUser user, TEnrollment enrollment) {
        public SessionUser User = user;
        public TEnrollment Enrollment = enrollment;
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

    /// <summary>Null when it cannot be told: the user has no signed-in session to read their enrollment through,
    /// or reading it failed</summary>
    public bool? HasEnrollment(string sid) {
        try {
            return SessionOf(sid) is { } user ? host.HasEnrollment(user) : null;
        } catch (Exception ex) {
            log.Write($"enrollment check for {sid} failed: {ex.Message}");
            return null;
        }
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

    /// <summary>A new or changed enrollment: a process still running under the previous one is stopped
    /// first, since a running process is otherwise left as it is</summary>
    public async Task<DaemonStatus> EnrollAsync(string sid) {
        if (SessionOf(sid) is { } user) {
            TEnrollment? current;
            try {
                current = host.EnrollmentOf(user, out _);
            } catch (Exception) {
                current = null;
            }
            bool replaced;
            lock (_gate) replaced = current is not null && _entries.TryGetValue(sid, out var entry) && entry.Daemon is not null && !entry.Enrollment.Equals(current);
            if (replaced) {
                log.Write($"enroll: a changed {current!.Name} enrollment replaces the running one");
                await StopAsync(sid).ConfigureAwait(false);
            }
        }
        return Start(sid);
    }

    /// <summary>The graceful wait runs its course even while the service stops: cutting it short would end
    /// the job, and whatever a graceful stop was saving with it</summary>
    public async Task<DaemonStatus> StopAsync(string sid) {
        Entry? entry;
        IRunningDaemon? daemon;
        lock (_gate) {
            if (!_entries.TryGetValue(sid, out entry)) return Status(sid);
            entry.WantRunning = false;
            if (entry.Daemon is null) {
                if (entry.State is DaemonState.Relaunching or DaemonState.Failed) entry.State = DaemonState.Stopped;
                return Describe(entry);
            }
            entry.State = DaemonState.Stopping;
            daemon = entry.Daemon;
        }

        try {
            host.RequestStop(entry.User, entry.Enrollment, daemon.Pid);
        } catch (Exception ex) {
            log.Write($"graceful stop of {entry.Enrollment.Name} could not be requested: {ex.Message}");
        }

        var finished = await Task.WhenAny(daemon.Exited, Task.Delay(gracefulStopWait, time)).ConfigureAwait(false);
        if (finished != daemon.Exited) {
            log.Write($"{entry.Enrollment.Name} (pid {daemon.Pid}) did not stop within {gracefulStopWait.TotalSeconds:0}s; ending it");
            daemon.Terminate();
        }
        var code = await daemon.Exited.ConfigureAwait(false);

        // Cleared here, not left to the watcher: a start that lands before the watcher runs would see the
        // stopped process as live and launch nothing
        lock (_gate) {
            if (ReferenceEquals(entry.Daemon, daemon)) {
                entry.Daemon = null;
                entry.LastExit = code;
                log.Write($"{entry.Enrollment.Name} pid {daemon.Pid} stopped with {code}");
            }
            if (entry.Daemon is null) entry.State = DaemonState.Stopped;
            return Describe(entry);
        }
    }

    /// <summary>Drops a user the service no longer supervises; their process is already stopped</summary>
    public void Forget(string sid) {
        lock (_gate) _entries.Remove(sid);
    }

    SessionUser? SessionOf(string sid) {
        foreach (var session in host.ActiveSessions()) {
            if (host.UserOf(session) is { } user && string.Equals(user.Sid, sid, StringComparison.OrdinalIgnoreCase)) return user;
        }
        return null;
    }

    /// <summary>Null when the session's user has a process (now or already); otherwise why not</summary>
    string? Ensure(int sessionId, string why, bool force) {
        SessionUser? user;
        TEnrollment? enrollment;
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
            log.Write($"{why}: session {sessionId} user {user.Sid} has nothing to run{(refusal is null ? "" : $": {refusal}")}");
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

        supervising?.Invoke(user.Sid);
        _ = WatchAsync(user.Sid, daemon);
        return null;
    }

    async Task WatchAsync(string sid, IRunningDaemon daemon) {
        var code = await daemon.Exited.ConfigureAwait(false);
        daemon.Dispose();
        if (_detached) return;

        int session;
        bool relaunch;
        Task? pause = null;
        lock (_gate) {
            if (!_entries.TryGetValue(sid, out var e) || !ReferenceEquals(e.Daemon, daemon)) return;
            e.Daemon = null;
            e.LastExit = code;
            relaunch = e.WantRunning && code != 0;
            // Counting from before the state reads Relaunching, so the pause is measured from that moment
            if (relaunch) pause = Task.Delay(RelaunchPause, time);
            e.State = relaunch ? DaemonState.Relaunching
                : e.State is DaemonState.SessionEnded ? DaemonState.SessionEnded
                : DaemonState.Stopped;
            session = e.User.SessionId;
            log.Write($"{e.Enrollment.Name} pid {daemon.Pid} exited with {code}{(relaunch ? $"; relaunching in {RelaunchPause.TotalSeconds:0}s" : "")}");
        }
        if (pause is null) return;

        await pause.ConfigureAwait(false);
        lock (_gate) {
            if (_detached || !_entries.TryGetValue(sid, out var e) || !e.WantRunning) return;
        }
        Ensure(session, "relaunch", force: false);
    }

    static DaemonStatus Describe(Entry e) => new(
        e.State, e.Enrollment.Name, e.Daemon?.Pid, e.User.SessionId, e.Launches, e.LastExit, e.Adopted,
        e.Daemon?.HasJob ?? false, e.Error);
}
