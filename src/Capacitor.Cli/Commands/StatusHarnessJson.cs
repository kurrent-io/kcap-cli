namespace Capacitor.Cli.Commands;

/// <param name="InstallCommand">What to run to wire this one up, when it is installed and unwired.</param>
/// <param name="LastHookEvent">When this agent last ran a kcap hook on this machine; null when wired
/// but not yet run, and always null when unwired.</param>
public sealed record StatusHarnessJson(
    string Vendor, bool Installed, bool Wired, string? InstallCommand, DateTimeOffset? LastHookEvent = null);
