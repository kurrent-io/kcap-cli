namespace Capacitor.Cli.Commands;

/// <summary>A flow ensure's result, and what became of the service a renamed daemon left behind:
/// <see cref="Retired"/> when its unit is gone, <see cref="LeftInstalled"/> when it is still there beside
/// a running new one.</summary>
sealed record FlowEnsure(ServiceEnsureJson Result, string? Retired = null, string? LeftInstalled = null) {
    public static bool IsUp(ServiceEnsureJson result) => result.Outcome is "installed" or "started" or "already_enabled";
}
