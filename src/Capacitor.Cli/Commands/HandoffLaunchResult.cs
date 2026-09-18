namespace Capacitor.Cli.Commands;

internal enum HandoffLaunchStatus { Ran, LaunchFailed }

/// <summary>What launching the chosen agent came to. <see cref="ExitCode"/> is set whenever the
/// process actually ran, whichever <see cref="Status"/> that exit sorted into.</summary>
internal sealed record HandoffLaunchResult(HandoffLaunchStatus Status, int? ExitCode, string? Error);
