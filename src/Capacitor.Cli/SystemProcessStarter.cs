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

        if (psi.RedirectStandardInput) process.StandardInput.Close();
        if (psi.RedirectStandardOutput) process.StandardOutput.Close();
        if (psi.RedirectStandardError) process.StandardError.Close();

        return process.Id;
    }
}
