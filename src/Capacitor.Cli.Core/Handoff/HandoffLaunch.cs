namespace Capacitor.Cli.Core.Handoff;

/// <summary>The hand-off a launch carries out. <paramref name="SessionCommits"/> is null when the server's
/// commit scan failed.</summary>
public sealed record HandoffLaunch(
    HandoffLaunchStart Start,
    HandoffSourceContext Source,
    IReadOnlyList<string>? SessionCommits
);
