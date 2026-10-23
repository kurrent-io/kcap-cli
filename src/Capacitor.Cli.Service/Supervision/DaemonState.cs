namespace Capacitor.Cli.Service.Supervision;

public enum DaemonState {
    NotEnrolled,
    NoSession,
    Running,
    Relaunching,
    Stopping,
    Stopped,
    SessionEnded,
    Failed,
}
