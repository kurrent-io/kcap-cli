using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon;

/// <summary>
/// redirect the daemon's OS-level stdout/stderr (fds 1 and 2) onto a
/// file so output that BYPASSES the ILogger pipeline is still captured — the
/// runtime's "Fatal error." dump on an unhandled native exception, an
/// <c>abort()</c> message, or a <see cref="Environment.FailFast(string)"/>.
///
/// <para>The detached launch path (<c>kcap daemon start -d</c> / <c>kcap
/// agent</c>) redirects the child's std streams to a pipe and immediately
/// closes it (, to avoid a pipe-leak hang), so without this those fatal
/// messages are written to a broken pipe and lost — which is exactly why a
/// hard daemon death currently leaves no trace. The CLI therefore passes
/// <c>--stderr-file &lt;path&gt;</c> on the detached path; the daemon reopens
/// its fds onto that file here. A supervised launchd service already captures
/// stderr via <c>StandardErrorPath</c>, so the CLI does NOT pass the flag
/// there and this stays a no-op.</para>
///
/// <para>No-op on Windows (fd redirection is POSIX-specific) and best-effort
/// everywhere: a failure to redirect must never stop the daemon from starting.
/// Managed <c>Console.Error</c> is intentionally left alone — the whole point
/// is to catch writes that go straight to fd 2 beneath the CLR.</para>
/// </summary>
internal static partial class StdErrCapture {
    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    // Root the backing stream for the process lifetime. dup2 shares the open
    // file description, so fd 2 would survive this being collected, but keeping
    // it alive avoids the finalizer closing the underlying handle out from under
    // us and makes the ownership obvious.
    static FileStream? _capture;

    /// <summary>
    /// The file to redirect onto, or null to skip: blank/unset arg, or Windows
    /// (where the daemon relies on the service host / a different mechanism).
    /// </summary>
    public static string? ResolveTarget(string? stderrFileArg) =>
        OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(stderrFileArg)
            ? null
            : stderrFileArg;

    /// <summary>
    /// Point fds 1 and 2 at <paramref name="path"/> (append mode). Best-effort;
    /// returns true only if both redirect syscalls succeeded. Call once, as
    /// early as possible in startup, so even a crash during host construction
    /// is captured.
    /// </summary>
    public static bool Apply(string path) {
        try {
            // This runs before the logging provider creates the config dir, so on a
            // fresh install/profile (or a custom KCAP_CONFIG_DIR) the parent dir may
            // not exist yet — without this the open fails and capture is silently
            // disabled for the whole daemon lifetime. Best-effort like the rest.
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _capture = fs; // keep alive for the process lifetime
            var fd = (int)fs.SafeFileHandle.DangerousGetHandle();

            // stdout then stderr onto the same open file description.
            return dup2(fd, 1) != -1 && dup2(fd, 2) != -1;
        } catch {
            // Directory missing, permission denied, redirected to a closed pipe,
            // etc. Already best-effort — never let this abort daemon startup.
            return false;
        }
    }
}
