using System.Diagnostics;

namespace Capacitor.Cli;

/// <summary>
/// Starts a child process. Injected rather than called statically because every spawn behind it is
/// fire-and-forget inside a catch-all: a refused start and a thrown one leave the same observable
/// nothing, so only the starter itself can say which happened.
/// </summary>
public interface IProcessStarter {
    Process? Start(ProcessStartInfo psi);
}
