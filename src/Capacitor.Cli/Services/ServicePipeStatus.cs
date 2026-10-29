namespace Capacitor.Cli.Services;

/// <summary>The service's view of the caller's daemon. <see cref="State"/> is one of the service's wire
/// words; one this CLI does not know reads as not running</summary>
sealed record ServicePipeStatus(
    string State, string? Name = null, int? Pid = null, int? SessionId = null, int Launches = 0, int? LastExit = null,
    bool Adopted = false, bool HasJob = false, string? Error = null) {
    public bool Running => State is "running" or "relaunching";
}
