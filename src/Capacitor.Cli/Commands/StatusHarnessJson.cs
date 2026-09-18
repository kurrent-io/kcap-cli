namespace Capacitor.Cli.Commands;

/// <param name="InstallCommand">What to run to wire this one up, when it is installed and unwired.</param>
public sealed record StatusHarnessJson(
    string Vendor, bool Installed, bool Wired, string? InstallCommand);
