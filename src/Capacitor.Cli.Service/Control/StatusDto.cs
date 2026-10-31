using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Control;

public sealed record StatusDto(
    string State, string? Name, int? Pid, int? SessionId, int Launches, int? LastExit, bool Adopted, bool HasJob, string? Error) {
    public static StatusDto From(DaemonStatus s) =>
        new(StateWord(s.State), s.Name, s.Pid, s.SessionId, s.Launches, s.LastExit, s.Adopted, s.HasJob, s.Error);

    /// <summary>The wire words are a contract with older and newer CLIs: add, never rename</summary>
    public static string StateWord(DaemonState state) => state switch {
        DaemonState.NotEnrolled  => "not_enrolled",
        DaemonState.NoSession    => "no_session",
        DaemonState.Running      => "running",
        DaemonState.Relaunching  => "relaunching",
        DaemonState.Stopping     => "stopping",
        DaemonState.Stopped      => "stopped",
        DaemonState.SessionEnded => "session_ended",
        DaemonState.Failed       => "failed",
        _                        => "unknown",
    };
}
