using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// SIGKILLs a process and every descendant, children before parents, each verified by start identity.
/// <para><c>Process.Kill(entireProcessTree: true)</c> SIGSTOPs each process before it looks for children.
/// A stopped member of the daemon's own process group is what makes the kernel hang up the whole
/// group, the daemon included, the moment that group becomes orphaned: on macOS a launch agent shares
/// launchd's session, so the exit of a grandchild that was reparented to launchd orphans the daemon's
/// group, and every <c>Process.Start</c> child (Pi, Antigravity, ACP agents, git) lives in that group.
/// The runtime's tree kill is banned in this assembly for that reason; Windows has neither process
/// groups nor SIGSTOP, so it still uses it.</para>
/// <para>Nothing is stopped here, so a parent can fork between a listing and its own death, and that
/// child is reparented out of reach. Killing children first and re-listing the parent until it shows
/// nothing new keeps the window to the two syscalls between the last listing and the parent's
/// SIGKILL. A pid is signalled only while it still carries the identity captured when it was listed,
/// and never twice, so a recycled pid is never a target.</para>
/// </summary>
internal static partial class ProcessTree {
    /// <summary>Re-listings of a parent after its known children are dead, before it is killed
    /// regardless; bounds a parent that keeps forking.</summary>
    const int MaxRescans = 3;

    public static void Kill(Process process) {
        try { if (process.HasExited) return; } catch (InvalidOperationException) { return; }

        if (OperatingSystem.IsWindows()) {
#pragma warning disable RS0030 // no SIGSTOP on Windows, so the runtime's tree kill is the right tool there
            process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
            return;
        }

        Kill(process.Id);
    }

    public static void Kill(int pid) {
        if (pid <= 0) return;

        if (OperatingSystem.IsWindows()) {
            try { using var process = Process.GetProcessById(pid); Kill(process); }
            catch (ArgumentException) { /* already gone */ }
            return;
        }

        if (ProcessIdentity.Capture(pid) is { } identity) KillSubtree(pid, identity);
    }

    static void KillSubtree(int pid, string identity) {
        var seen = new HashSet<int>();

        for (var rescan = 0; rescan <= MaxRescans; rescan++) {
            var fresh = false;

            foreach (var child in Children(pid)) {
                if (!seen.Add(child)) continue; // handled on an earlier pass; a zombie stays listed until its parent dies

                fresh = true;
                if (ProcessIdentity.Capture(child) is { } childIdentity) KillSubtree(child, childIdentity);
            }

            if (!fresh) break;
        }

        if (ProcessIdentity.Matches(pid, identity)) UnixPtyInterop.kill(pid, UnixPtyInterop.SIGKILL);
    }

    static IEnumerable<int> Children(int parent) =>
        OperatingSystem.IsMacOS() ? MacChildren(parent) : LinuxChildren(parent);

    // ── macOS: libproc lists a process's children directly ───────────────────────────────────────

    [LibraryImport("libproc", EntryPoint = "proc_listchildpids")]
    private static unsafe partial int proc_listchildpids(int ppid, int* buffer, int buffersize);

    /// <summary>Returns a count of pids, not bytes; a count equal to the capacity means the buffer
    /// was too small.</summary>
    static unsafe int[] MacChildren(int parent) {
        var buffer = new int[256];

        while (true) {
            int count;
            fixed (int* p = buffer) count = proc_listchildpids(parent, p, buffer.Length * sizeof(int));

            if (count < 0) return [];
            if (count < buffer.Length) return buffer[..count];

            buffer = new int[buffer.Length * 2];
        }
    }

    // ── Linux: the ppid field of every /proc/{pid}/stat ───────────────────────────────────────────

    static IEnumerable<int> LinuxChildren(int parent) {
        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            if (LinuxParentOf(pid) == parent) yield return pid;
        }
    }

    /// <summary>The token after the state, which follows the last <c>)</c> because the command name
    /// can contain spaces and parentheses.</summary>
    static int? LinuxParentOf(int pid) {
        try {
            var stat      = File.ReadAllText($"/proc/{pid}/stat");
            var afterComm = stat.LastIndexOf(')');
            if (afterComm < 0) return null;

            var fields = stat[(afterComm + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
                ? parent
                : null;
        } catch {
            return null; // the process left between the directory listing and the read
        }
    }
}
