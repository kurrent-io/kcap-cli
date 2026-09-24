namespace Capacitor.Cli.Daemon.Services;

/// <summary>A reap claim's outcome: the coded reason, and whether the claim landed while the runtime's
/// launch window was still open. Only a launch-window verdict is reported to the server as a launch
/// failure.</summary>
public sealed record TerminationVerdict(string Reason, bool ReapedInsideLaunchWindow);
