using System.Diagnostics;

namespace Capacitor.Cli;

/// <summary>Starts the child the way the runtime does.</summary>
public sealed class SystemProcessStarter : IProcessStarter {
    public static readonly SystemProcessStarter Instance = new();

    public Process? Start(ProcessStartInfo psi) => Process.Start(psi);

    /// <summary>
    /// Windows spawns through <c>CreateProcess</c> with <c>bInheritHandles: false</c>, the
    /// only cut that keeps an agent's pipes out of a detached child — see
    /// <see cref="ProcessHelpers.StartDetachedWindows"/> for why the alternatives don't hold.
    /// Unix marks its own pipe descriptors close-on-exec instead, which <c>exec</c> honours,
    /// and hands back the parent's ends of the redirect pipes.
    /// </summary>
    public int? StartDetached(ProcessStartInfo psi) {
        if (OperatingSystem.IsWindows()) {
            return ProcessHelpers.StartDetachedWindows(psi);
        }

        ProcessHelpers.PreventInheritedHandles();

        if (Process.Start(psi) is not { } process) {
            return null;
        }

        // Disposing the wrapper releases this process's handle on the child; it does not
        // signal the child, which goes on running detached. Nothing here waits on it, so an
        // undisposed wrapper would hold that handle until finalization.
        using (process) {
            if (psi.RedirectStandardInput) process.StandardInput.Close();
            if (psi.RedirectStandardOutput) process.StandardOutput.Close();
            if (psi.RedirectStandardError) process.StandardError.Close();

            return process.Id;
        }
    }

    /// <summary>
    /// Windows names the single pipe handle in the child's inherit list, so nothing else crosses.
    /// Unix needs no equivalent: the sweep marks this process's pipe descriptors close-on-exec and
    /// <c>exec</c> honours that, leaving only the descriptors the redirect itself installs.
    /// </summary>
    public (int Pid, Stream StandardInput)? StartDetachedWithStdin(ProcessStartInfo psi) {
        if (OperatingSystem.IsWindows()) {
            return ProcessHelpers.StartDetachedWindowsWithStdin(psi);
        }

        ProcessHelpers.PreventInheritedHandles();

        if (Process.Start(psi) is not { } process) {
            return null;
        }

        if (psi.RedirectStandardOutput) process.StandardOutput.Close();
        if (psi.RedirectStandardError) process.StandardError.Close();

        // The wrapper cannot be disposed here — it owns the stdin pipe being handed back, and the
        // caller has not written the payload yet — so the returned stream owns it instead and
        // releases it on close.
        return (process.Id, new ChildStdinStream(process.StandardInput.BaseStream, process));
    }
}
