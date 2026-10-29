namespace Capacitor.App.ViewModels.Onboarding;

/// Whether the Done page may open the dashboard: the daemon must be running and the server must
/// have recorded this install, or the dashboard asks for an install the app already includes.
public enum WorkspaceReadiness { Preparing, Ready, DaemonNotRunning, Unreported }
