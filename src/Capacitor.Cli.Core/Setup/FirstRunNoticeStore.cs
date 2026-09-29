namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The one-shot marker setup leaves for the next session that starts with hooks in place. Nothing
/// here throws: a marker that cannot be written or taken costs a notice, never a setup or a hook.
/// </summary>
public sealed class FirstRunNoticeStore(ConfigRoot config) {
    const string MarkerFileName = "first-run-notice";

    readonly string _markerPath = config.Path(MarkerFileName);

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
    /// Takes the notice if it is there. The lock is what makes it one-shot: a bare delete races, and
    /// a rename picks a single winner only where the filesystem makes renaming atomic.
    ///
    /// <para>The unlocked probe keeps the lock off every session start with no notice waiting; the
    /// probe under the lock is the one that decides. <paramref name="lockTimeout"/> defaults to no
    /// wait, because this runs on the SessionStart hook path: a session that loses the race reports
    /// nothing, and the winner is already delivering the notice.</para>
    /// </summary>
    public bool TryClaim(TimeSpan? lockTimeout = null) {
        IDisposable lease;

        try {
            if (!File.Exists(_markerPath)) return false;

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
}
