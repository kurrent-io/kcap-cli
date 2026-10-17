namespace Capacitor.Cli.Commands;

/// <summary>A flow ensure's result, and what became of the service a renamed daemon left behind:
/// <see cref="Retired"/> when its unit is gone, <see cref="LeftInstalled"/> when it is still there.</summary>
sealed record FlowEnsure(ServiceEnsureJson Result, string? Retired = null, string? LeftInstalled = null);
