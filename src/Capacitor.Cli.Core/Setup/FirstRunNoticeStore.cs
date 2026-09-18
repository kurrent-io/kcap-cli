namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The one-shot marker that carries setup's closing hand-off across the restart it asks for.
///
/// <para>Hooks, plugin skills and MCP registrations are all read when an agent session starts, so
/// the session that ran setup has none of them: it is not recorded, and it cannot run the guided
/// tour. The restart is unavoidable, and the terminal line asking for it is the last thing in a run
/// that may have scrolled past — or, when a tool drove the install, one line inside a transcript it
/// has to remember to relay. This marker is what lets the NEXT session say it instead.</para>
///
/// <para>Claiming runs under the config lock, so exactly one session takes the notice however many
/// start at once. Nothing here throws: a marker that cannot be written costs a notice, and a notice
/// is not worth failing a setup over.</para>
/// </summary>
public sealed class FirstRunNoticeStore(ConfigRoot config) {
    const string MarkerFileName = "first-run-notice";

    readonly string _markerPath = config.Path(MarkerFileName);

    /// <summary>Leaves the notice for the next session that starts with hooks in place.</summary>
    public bool Arm() {
        try {
            var dir = Path.GetDirectoryName(_markerPath);

            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_markerPath, "");

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Takes the notice if it is there, and leaves nothing behind.
    ///
    /// <para>The lock is what makes it one-shot: a bare delete races, and a rename only decides a
    /// single winner where the filesystem makes renaming atomic, which is not something to rely on
    /// across platforms. <paramref name="lockTimeout"/> defaults to no wait at all, because this runs
    /// on the SessionStart hook path where the budget belongs to session capture: a session that
    /// loses the race reports nothing rather than holding the hook open, and the winner is already
    /// delivering the notice.</para>
    /// </summary>
    public bool TryClaim(TimeSpan? lockTimeout = null) {
        IDisposable lease;

        try {
            lease = config.AcquireLock(MarkerFileName, lockTimeout ?? TimeSpan.Zero);
        } catch {
            return false;
        }

        using (lease) {
            try {
                if (!File.Exists(_markerPath)) return false;

                File.Delete(_markerPath);

                return true;
            } catch {
                return false;
            }
        }
    }

    /// <summary>Whether a notice is waiting, without taking it. For <c>status</c>-shaped readers.</summary>
    public bool IsArmed() {
        try {
            return File.Exists(_markerPath);
        } catch {
            return false;
        }
    }
}
