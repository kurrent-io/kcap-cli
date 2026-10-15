namespace Capacitor.App.ViewModels.Onboarding;

/// What the earlier pages did, as the Done page reads it on every entry.
/// <param name="Recording">The harnesses whose capture installed.</param>
/// <param name="PathHazard">The login shell still cannot find kcap, so hooks record nothing.</param>
/// <param name="Import">The history import, while it runs and after; null when none started.</param>
/// <param name="WorkspaceUrl">The workspace this machine signed in to, or null.</param>
public sealed record DoneFacts(
    IReadOnlyList<string> Recording,
    bool                  CodexRecording,
    bool                  PathHazard,
    HistoryImportRun?     Import,
    bool                  DaemonRunning,
    string                MachineName,
    string?               WorkspaceUrl) {
    public static readonly DoneFacts Empty = new([], false, false, null, false, "this machine", null);
}
