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
/// <para>Claiming is an atomic rename, so exactly one session takes the notice however many start at
/// once. Nothing here throws: a marker that cannot be written costs a notice, and a notice is not
/// worth failing a setup over.</para>
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
    /// Takes the notice if it is there, and leaves nothing behind. The rename is the claim: two
    /// sessions starting together both attempt it, the loser's source no longer exists, and the
    /// notice is delivered once rather than twice.
    /// </summary>
    public bool TryClaim() {
        var claimed = $"{_markerPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.claimed";

        try {
            File.Move(_markerPath, claimed);
        } catch {
            return false;
        }

        // Best effort: the claim already happened above, and a leftover file here only wastes a few
        // bytes — it is never read.
        try { File.Delete(claimed); } catch { /* ignored */ }

        return true;
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
