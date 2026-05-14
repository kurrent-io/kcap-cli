using System.Diagnostics;

namespace kapacitor.Daemon;

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
/// <c>kapacitor agent doctor</c> reads it to surface human-friendly
/// "instance=<i>prefix</i>" output without needing a live SignalR session.</para>
///
/// <para>The acquisition guards against the AI-630 scenario: two daemons
/// under the same name on the same machine (regardless of
/// <c>KAPACITOR_CONFIG_DIR</c> — <see cref="AgentLockPaths"/> uses a fixed
/// directory). Two daemons with <i>different</i> names are allowed to
/// coexist; the lock file is per-name.</para>
/// </summary>
internal sealed class DaemonLock : IDisposable {
    readonly FileStream _stream;
    readonly string     _pidPath;
    readonly string     _lockPath;
    bool                _disposed;

    public string InstanceId { get; }

    DaemonLock(FileStream stream, string lockPath, string pidPath, string instanceId) {
        _stream    = stream;
        _lockPath  = lockPath;
        _pidPath   = pidPath;
        InstanceId = instanceId;
    }

    /// <summary>
    /// Try to acquire the per-name lock for <paramref name="daemonName"/>.
    /// Returns null when another live daemon already holds the lock — the
    /// caller should print a "name in use" message and exit with code 2.
    /// </summary>
    public static DaemonLock? TryAcquire(string daemonName) {
        AgentLockPaths.EnsureDirectory();

        var lockPath = AgentLockPaths.LockPath(daemonName);
        var pidPath  = AgentLockPaths.PidPath(daemonName);

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

        return new DaemonLock(stream, lockPath, pidPath, instanceId);
    }

    static void WritePidFile(string pidPath) {
        var process    = Process.GetCurrentProcess();
        long? startTicks = null;

        try {
            startTicks = process.StartTime.ToUniversalTime().Ticks;
        } catch {
            // Best-effort — StartTime can fail on some sandboxed/macOS
            // permission paths. The PID alone is still useful for stop.
        }

        var content = startTicks is { } t ? $"{process.Id}\n{t}" : process.Id.ToString();
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
        // AI-630 guard. `kapacitor agent doctor --clean` removes truly
        // stale files.
        //
        // Delete the PID file only if it still points to our own PID. A
        // legitimate successor daemon that ran while we were disposing
        // would have already rewritten the file to its own PID, and an
        // unconditional Delete here would orphan their entry.
        try {
            if (PidFileMatchesCurrentProcess(_pidPath)) File.Delete(_pidPath);
        } catch { /* best-effort */ }
    }

    static bool PidFileMatchesCurrentProcess(string pidPath) {
        try {
            if (!File.Exists(pidPath)) return false;

            var line = File.ReadAllText(pidPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return line is not null
                && int.TryParse(line, out var pid)
                && pid == Process.GetCurrentProcess().Id;
        } catch {
            return false;
        }
    }
}
