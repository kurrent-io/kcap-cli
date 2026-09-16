using System.Diagnostics;

namespace Capacitor.Cli;

/// <summary>
/// Starts a child process. Injected rather than called statically because every spawn behind it is
/// fire-and-forget inside a catch-all: a refused start and a thrown one leave the same observable
/// nothing, so only the starter itself can say which happened.
/// </summary>
public interface IProcessStarter {
    Process? Start(ProcessStartInfo psi);

    /// <summary>
    /// Starts a child that outlives this process, returning its pid (null if the spawn
    /// failed). Use this rather than <see cref="Start"/> for anything detached: such a child
    /// must carry none of this process's handles, and when this process is a coding-agent
    /// hook, those include the pipe the agent reads the hook's output from. The caller gets a
    /// pid rather than a <see cref="Process"/> because there are no redirected streams to
    /// own — that redirection is itself what forces the inheritance.
    /// </summary>
    int? StartDetached(ProcessStartInfo psi);
}
