namespace kapacitor;

/// <summary>
/// One-shot migration from the pre-AI-630 singleton layout
/// (<c>~/.config/kapacitor/agent.pid</c>,
/// <c>~/.config/kapacitor/agent.start.lock</c>) to the per-name layout
/// (<c>~/.config/kapacitor/agents/&lt;name&gt;.pid</c>, …).
///
/// <para>Called from both the daemon binary (before acquiring its own lock)
/// and the CLI supervisor (before taking the start lock). Best-effort:
/// any IO failure is swallowed because the new path will be created
/// fresh anyway — the migration is convenience, not correctness.</para>
///
/// <para>Idempotent. Subsequent calls find no legacy files and no-op.</para>
/// </summary>
public static class AgentLockMigration {
    /// <summary>
    /// Returns the legacy paths under <c>PathHelpers.ConfigPath</c>. Defined
    /// here (not in <see cref="AgentLockPaths"/>) so the path layout helper
    /// stays focused on the current layout and doesn't accidentally suggest
    /// the legacy paths are valid targets.
    /// </summary>
    static string LegacyPidPath  => PathHelpers.ConfigPath("agent.pid");
    static string LegacyLockPath => PathHelpers.ConfigPath("agent.start.lock");

    /// <summary>
    /// Migrates legacy files (if present) under <paramref name="daemonName"/>.
    /// Returns the list of files moved so the caller can surface a one-line
    /// info message. Safe to call repeatedly — no-ops after the first run.
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacyFiles(string daemonName) =>
        MigrateLegacyFiles(daemonName, LegacyPidPath, LegacyLockPath);

    /// <summary>
    /// Test-friendly overload that takes explicit legacy paths. Production
    /// callers use the parameterless <see cref="MigrateLegacyFiles(string)"/>
    /// overload, which reads <see cref="PathHelpers.ConfigPath"/>; tests
    /// pass scratch paths so they don't depend on (and can't pollute)
    /// <c>PathHelpers</c>'s once-cached <c>ConfigDir</c>.
    /// </summary>
    internal static IReadOnlyList<string> MigrateLegacyFiles(string daemonName, string legacyPidPath, string legacyLockPath) {
        var moved = new List<string>(2);

        AgentLockPaths.EnsureDirectory();

        TryMove(legacyPidPath,  AgentLockPaths.PidPath(daemonName),       moved);
        TryMove(legacyLockPath, AgentLockPaths.StartLockPath(daemonName), moved);

        return moved;
    }

    static void TryMove(string from, string to, List<string> moved) {
        try {
            if (!File.Exists(from)) return;
            if (File.Exists(to)) {
                // New path already populated — drop the legacy file rather
                // than overwrite (the new file is authoritative). The
                // pre-AI-630 daemon that wrote the legacy file is presumed
                // dead since the new daemon couldn't have written `to`
                // otherwise.
                File.Delete(from);
                return;
            }

            File.Move(from, to);
            moved.Add($"{from} → {to}");
        } catch {
            // Best-effort. A failed migration shouldn't block the daemon
            // (or CLI) from starting — the new path will be created fresh
            // either way. We don't surface this to console because the
            // daemon's logger may not be initialised yet when this runs.
        }
    }
}
