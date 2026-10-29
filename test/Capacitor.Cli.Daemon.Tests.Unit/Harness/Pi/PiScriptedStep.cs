namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>One scripted turn for <see cref="PiScriptedProvider"/>: either a plain assistant reply
/// (<see cref="Text"/>) or a forced tool call (<see cref="Tool"/> plus <see cref="ArgsJson"/>) the
/// model would never volunteer on its own — the only way to measure whether a tool is reachable
/// rather than merely unused.</summary>
internal sealed record PiScriptedStep(string? Text = null, string? Tool = null, string? ArgsJson = null, string Id = "call");
