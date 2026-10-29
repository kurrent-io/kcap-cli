namespace Capacitor.Cli.Service.Supervision;

public sealed record DaemonStatus(
    DaemonState State,
    string? Name = null,
    int? Pid = null,
    int? SessionId = null,
    int Launches = 0,
    int? LastExit = null,
    bool Adopted = false,
    bool HasJob = false,
    string? Error = null);
