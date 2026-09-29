namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>Result of one <see cref="PiContainmentBench.RunAsync"/> launch: what the scripted
/// provider observed and what <c>get_state</c> reported, read off the wire rather than trusted from
/// the model's own account.</summary>
internal sealed record PiContainmentRun(
    bool Ready, IReadOnlyList<string> ToolsOffered, string FirstRequestText,
    IReadOnlyList<string> ToolResults, string? SessionFile, string Stderr);
