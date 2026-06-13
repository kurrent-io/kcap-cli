using System.Diagnostics;

namespace Capacitor.Cli.Core;

static class CliProcess {
    /// <summary>
    /// Starts a child process, converting a failed launch into a logged
    /// <c>null</c> instead of letting the exception escape.
    ///
    /// <para>
    /// A bare <see cref="Process.Start(ProcessStartInfo)"/> throws
    /// <see cref="System.ComponentModel.Win32Exception"/> when the target
    /// executable can't be found (e.g. <c>claude</c> not on PATH). In a
    /// detached hook / what's-done process that exception escapes to
    /// <c>Main</c> and aborts the whole CLI with SIGABRT — observed as a
    /// what's-done summary crash when <c>claude</c> was missing from the
    /// reparented process's PATH. Funnelling the failure to <c>null</c> lets
    /// callers fall through their existing "process is null → return null"
    /// handling and exit gracefully.
    /// </para>
    /// </summary>
    internal static Process? TryStart(ProcessStartInfo psi, Action<string> log) {
        try {
            return Process.Start(psi);
        } catch (Exception ex) {
            log($"Failed to start {psi.FileName}: {ex.Message}");

            return null;
        }
    }
}
