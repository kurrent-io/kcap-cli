using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// SIGKILLs a process and every descendant without stopping any of them first.
/// <para><c>Process.Kill(entireProcessTree: true)</c> SIGSTOPs each process before it looks for children. A stopped
/// member of the daemon's own process group is what makes the kernel hang up the whole group, the daemon
/// included, the moment that group becomes orphaned. On macOS a launch agent shares launchd's session, so
/// the exit of a grandchild that was reparented to launchd orphans the daemon's group, and every
/// <c>Process.Start</c> child (Pi, Antigravity, ACP agents, git) lives in that group. The runtime's tree
/// kill is banned in this assembly for that reason; Windows has neither process groups nor SIGSTOP, so it
/// still uses it.</para>
/// </summary>
internal static partial class ProcessTree {
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

        var doomed = new HashSet<int> { pid };

        // A process can fork between the walk and its kill, and its children are reparented away the
        // moment it dies, so walk again after killing until a pass finds nothing new.
        for (var pass = 0; pass < 3; pass++) {
            var grew = doomed.UnionWithCount(DescendantsOf(doomed)) > 0;

            foreach (var p in doomed) UnixPtyInterop.kill(p, UnixPtyInterop.SIGKILL);

            if (!grew && pass > 0) break;
        }
    }

    /// <summary>Every descendant of <paramref name="pid"/>: children, grandchildren and so on, never
    /// <paramref name="pid"/> itself.</summary>
    internal static IReadOnlySet<int> Descendants(int pid) => DescendantsOf([pid]);

    static HashSet<int> DescendantsOf(IReadOnlyCollection<int> roots) {
        var found = new HashSet<int>();
        if (roots.Count == 0) return found;

        var childrenOf = OperatingSystem.IsMacOS() ? MacChildren : LinuxChildrenSnapshot();
        var queue      = new Queue<int>(roots);

        while (queue.TryDequeue(out var parent))
            foreach (var child in childrenOf(parent))
                if (!roots.Contains(child) && found.Add(child)) queue.Enqueue(child);

        return found;
    }

    static int UnionWithCount(this HashSet<int> set, IEnumerable<int> items) {
        var added = 0;
        foreach (var item in items) if (set.Add(item)) added++;
        return added;
    }

    // ── macOS: libproc lists a process's children directly ───────────────────────────────────────

    [LibraryImport("libproc", EntryPoint = "proc_listchildpids")]
    private static unsafe partial int proc_listchildpids(int ppid, int* buffer, int buffersize);

    /// <summary>Returns a count of pids, not bytes; a count equal to the capacity means the buffer
    /// was too small.</summary>
    static unsafe IEnumerable<int> MacChildren(int parent) {
        var buffer = new int[256];

        while (true) {
            int count;
            fixed (int* p = buffer) count = proc_listchildpids(parent, p, buffer.Length * sizeof(int));

            if (count < 0) return [];
            if (count < buffer.Length) return buffer[..count];

            buffer = new int[buffer.Length * 2];
        }
    }

    // ── Linux: one /proc scan, then a parent → children map ───────────────────────────────────────

    static Func<int, IEnumerable<int>> LinuxChildrenSnapshot() {
        var childrenOf = new Dictionary<int, List<int>>();

        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            if (LinuxParentOf(pid) is not { } parent) continue;

            if (!childrenOf.TryGetValue(parent, out var list)) childrenOf[parent] = list = [];
            list.Add(pid);
        }

        return parent => childrenOf.TryGetValue(parent, out var list) ? list : [];
    }

    /// <summary>The ppid field of <c>/proc/{pid}/stat</c>: the token after the state, which follows
    /// the last <c>)</c> because the command name can contain spaces and parentheses.</summary>
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
