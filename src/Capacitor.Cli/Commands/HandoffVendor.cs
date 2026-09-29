using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

internal sealed record HandoffVendor(HarnessId Id, string Label, string? Executable) {
    public bool Launchable => Executable is not null;
}
