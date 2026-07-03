using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon;

/// <summary>
/// Per-daemon-name exclusive lock and PID file (AI-630). Acquired by the
/// daemon process for its lifetime; release happens automatically when the
/// <see cref="FileStream"/> handle closes (process exit, including
/// <c>SIGKILL</c> / power-off — the kernel releases <c>flock</c>).
///
/// <para>The lock content is the daemon's <c>InstanceId</c> (a fresh GUID
/// generated at startup). The server uses the same GUID — sent over
/// <c>DaemonConnect</c> — to distinguish a same-process reconnect from a
/// different-process collision. The lock file's content is therefore
/// diagnostic only (the wire <c>InstanceId</c> is the authoritative copy);
/// <c>kcap daemon doctor</c> reads it to surface human-friendly
/// "instance=<i>prefix</i>" output without needing a live SignalR session.</para>
///
/// <para>The acquisition guards against the AI-630 scenario: two daemons
/// under the same name on the same machine (regardless of
/// <c>KCAP_CONFIG_DIR</c> — <see cref="DaemonLockPaths"/> uses a fixed
/// directory). Two daemons with <i>different</i> names are allowed to
/// coexist; the lock file is per-name.</para>
/// </summary>
internal sealed class DaemonLock : IDisposable {
    readonly FileStream _stream;
    readonly string     _pidPath;
    readonly string     _lockPath;
    readonly string     _versionPath;
    bool                _disposed;

    public string InstanceId { get; }

    /// <summary>
    /// True when a PID file for this name was still on disk at the moment we
    /// acquired the exclusive flock (AI-1155). A graceful shutdown deletes the
    /// PID file in <see cref="Dispose"/>, so a leftover one means the previous
    /// holder exited WITHOUT running its cleanup — the signature of an
    /// uncatchable termination (external <c>SIGKILL</c> from macOS jetsam/OOM
    /// or <c>kill -9</c>, a power loss, or a hard native crash), none of which
    /// a signal handler inside the dying process can log. The successor logs a
    /// startup breadcrumb so the otherwise-silent death leaves a trace.
    /// </summary>
    public bool PriorExitWasUnclean { get; }

    /// <summary>
    /// The PID recorded in the leftover PID file, when <see cref="PriorExitWasUnclean"/>
    /// is true and the file's first line parsed as an integer; otherwise null.
    /// </summary>
    public int? PriorHolderPid { get; }

    DaemonLock(FileStream stream, string lockPath, string pidPath, string versionPath, string instanceId,
               bool priorExitWasUnclean, int? priorHolderPid) {
        _stream             = stream;
        _lockPath           = lockPath;
        _pidPath            = pidPath;
        _versionPath        = versionPath;
        InstanceId          = instanceId;
        PriorExitWasUnclean = priorExitWasUnclean;
        PriorHolderPid      = priorHolderPid;
    }

    /// <summary>
    /// Try to acquire the per-name lock for <paramref name="daemonName"/>.
    /// Returns null when another live daemon already holds the lock — the
    /// caller should print a "name in use" message and exit with code 2.
    ///
    /// <para>When <paramref name="version"/> is supplied, also writes the
    /// freely-readable <c>&lt;name&gt;.version</c> marker so <c>kcap daemon
    /// status</c> can report the running daemon's version.</para>
    /// </summary>
    public static DaemonLock? TryAcquire(string daemonName, string? version = null) {
        DaemonLockPaths.EnsureDirectory();

        var lockPath    = DaemonLockPaths.LockPath(daemonName);
        var pidPath     = DaemonLockPaths.PidPath(daemonName);
        var versionPath = DaemonLockPaths.VersionPath(daemonName);

        FileStream stream;

        try {
            // FileShare.None maps to flock(LOCK_EX) on POSIX. Open with
            // FileAccess.Write so we can rewrite the instance id into the
            // file content below. FileMode.OpenOrCreate keeps a stale
            // lockfile on disk from blocking acquisition — flock semantics
            // mean kernel-managed liveness, not file presence.
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            return null;
        }

        var instanceId = Guid.NewGuid().ToString("N");

        // AI-1155: inspect any leftover PID file BEFORE WritePidFile overwrites
        // it. Now that we hold the exclusive flock the prior holder is provably
        // gone, so a still-present PID file means it never ran Dispose (which
        // deletes it) — i.e. it died via an uncatchable SIGKILL / crash. Capture
        // that for the successor's startup breadcrumb.
        var (priorUnclean, priorPid) = InspectPriorHolder(pidPath);

        try {
            // Rewrite the file content with the fresh instance id. Truncate
            // first so a smaller new id doesn't leave trailing bytes from
            // the previous holder.
            stream.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes(instanceId + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            WritePidFile(pidPath);
        } catch {
            stream.Dispose();
            throw;
        }

        // Overwrite (atomically) any stale version marker with ours. A successor
        // of a restart-after-update lands here and refreshes it to the new
        // binary's version. Best-effort and OUTSIDE the fatal try above: the
        // marker is observability-only (`kcap daemon status`), so an IO failure
        // writing it must never abort acquisition the way a lock/pid failure does.
        if (version is not null) {
            try { DaemonVersionMarker.Write(daemonName, version); } catch { /* best-effort */ }
        }

        return new DaemonLock(stream, lockPath, pidPath, versionPath, instanceId, priorUnclean, priorPid);
    }

    /// <summary>
    /// Reads a leftover PID file (call only while holding the flock, before
    /// overwriting it). Returns (unclean: whether the file was present at all,
    /// pid: its parsed first line if any). A present file ⇒ the prior holder
    /// skipped its cleanup ⇒ unclean exit; the PID may be unparseable, in which
    /// case we still report unclean but with a null PID.
    /// </summary>
    static (bool unclean, int? pid) InspectPriorHolder(string pidPath) {
        try {
            if (!File.Exists(pidPath)) return (false, null);

            var first = File.ReadAllText(pidPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return (true, int.TryParse(first, out var pid) ? pid : null);
        } catch {
            // Can't read it — don't fabricate a breadcrumb.
            return (false, null);
        }
    }

    /// <summary>
    /// Like <see cref="TryAcquire(string, string?)"/> but retries until <paramref name="awaitTimeout"/>
    /// elapses — used by a self-respawned successor (<c>--await-lock</c>) to wait out the
    /// outgoing daemon's flock instead of exiting with code 2 on the first contended attempt.
    /// </summary>
    public static DaemonLock? TryAcquire(string daemonName, TimeSpan awaitTimeout, string? version = null) {
        var deadline = DateTime.UtcNow + awaitTimeout;

        while (true) {
            if (TryAcquire(daemonName, version) is { } locked) return locked;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(100);
        }
    }

    static void WritePidFile(string pidPath) {
        // The second line is a cross-process-stable start token (AI-839): on
        // Linux Process.StartTime differs between the daemon that writes it and
        // the CLI that later reads it, so we persist the kernel's boot-relative
        // starttime instead. Best-effort — a null token falls back to PID-only,
        // and the CLI's IsOurDaemon then uses a process-name check.
        var token   = ProcessStartToken.ForCurrent();
        var content = token is not null ? $"{Environment.ProcessId}\n{token}" : Environment.ProcessId.ToString();
        File.WriteAllText(pidPath, content);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        // Releasing the FileStream releases the kernel-level flock. After
        // that, a follow-up start with the same name can acquire cleanly.
        try { _stream.Dispose(); } catch { /* best-effort */ }

        // Do NOT delete the lock file. The kernel flock is what enforces
        // exclusion; file presence on disk is irrelevant. Deleting it here
        // races against another daemon that may already have acquired the
        // path between our Dispose() and the unlink: our `File.Delete`
        // would unlink the inode they're holding open, and a third daemon
        // could then create a brand-new `<name>.lock` at the same path
        // and acquire a SECOND independent flock — defeating the whole
        // AI-630 guard. `kcap daemon doctor --clean` removes truly
        // stale files.
        //
        // Delete the PID file only if it still points to our own PID. A
        // legitimate successor daemon that ran while we were disposing
        // would have already rewritten the file to its own PID, and an
        // unconditional Delete here would orphan their entry. The version
        // marker follows the SAME ownership guard: if a successor has taken
        // over the name it has already written its own version marker, so
        // deleting here would clobber the successor's fresh value.
        bool ours;
        try { ours = PidFileMatchesCurrentProcess(_pidPath); } catch { ours = false; }

        if (ours) {
            try { File.Delete(_pidPath); } catch { /* best-effort */ }
            try { File.Delete(_versionPath); } catch { /* best-effort */ }
        }
    }

    static bool PidFileMatchesCurrentProcess(string pidPath) {
        try {
            if (!File.Exists(pidPath)) return false;

            var line = File.ReadAllText(pidPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return line is not null
                && int.TryParse(line, out var pid)
                && pid == Environment.ProcessId;
        } catch {
            return false;
        }
    }
}
