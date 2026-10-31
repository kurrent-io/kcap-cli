namespace Capacitor.App.ViewModels.Onboarding;

/// What the earlier pages did, as the Done page reads it on every entry.
/// <param name="Recording">The agents whose hooks installed.</param>
/// <param name="PathHazard">The login shell still cannot find kcap, so hooks store nothing.</param>
/// <param name="Import">The history import, while it runs and after; null when none started.</param>
/// <param name="WorkspaceUrl">The workspace this machine signed in to, or null.</param>
/// <param name="DaemonProblem">Why the daemon is not running, or null while it is being set up.</param>
/// <param name="Local">The machine runs its own local server, so the page names no terminal command.</param>
/// <param name="DashboardUrl">The local server's web dashboard, or null when unknown.</param>
/// <param name="DaemonBusy">An attempt to start the service is in flight.</param>
/// <param name="ServiceDetail">What the daemon's last outcome reported beyond the problem itself, or null.</param>
/// <param name="PathAdded">This setup put kcap on the terminal's PATH, which only terminals opened afterwards see.</param>
public sealed record DoneFacts(
    IReadOnlyList<string> Recording,
    bool                  CodexRecording,
    bool                  PathHazard,
    HistoryImportRun?     Import,
    bool                  DaemonRunning,
    string                MachineName,
    string?               WorkspaceUrl,
    string?               DaemonProblem = null,
    bool                  Local = false,
    string?               DashboardUrl = null,
    bool                  DaemonBusy = false,
    string?               ServiceDetail = null,
    bool                  PathAdded = false) {
    public static readonly DoneFacts Empty = new([], false, false, null, false, "this machine", null);
}
