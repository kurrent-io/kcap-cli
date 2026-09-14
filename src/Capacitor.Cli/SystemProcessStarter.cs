using System.Diagnostics;

namespace Capacitor.Cli;

/// <summary>Starts the child the way the runtime does.</summary>
public sealed class SystemProcessStarter : IProcessStarter {
    public static readonly SystemProcessStarter Instance = new();

    public Process? Start(ProcessStartInfo psi) => Process.Start(psi);
}
