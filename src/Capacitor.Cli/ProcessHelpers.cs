using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Capacitor.Cli;

static partial class ProcessHelpers {
    // POSIX errno values used by IsProcessAliveUnix.
    // kill(pid, 0) returns 0 if the process exists and we can signal it,
    // -1 with errno = EPERM (1) if it exists but we can't signal it,
    // -1 with errno = ESRCH (3) if no such process.
    const int EPERM = 1;

    [LibraryImport("libc", EntryPoint = "getppid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int getppid_native();

    [LibraryImport("libc", EntryPoint = "getpgrp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int getpgrp_native();

    [LibraryImport("libc", EntryPoint = "getpid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int getpid_native();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int kill_native(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int setsid_native();

    // macOS: proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info)) fills a
    // proc_bsdinfo struct. We read pbi_ppid (offset 16) for the parent PID and only
    // FALL BACK to pbi_name (offset 64, 32 bytes) for the process name — the primary
    // name source is the exec path from KERN_PROCARGS2 (see ReadExecPathMac), because
    // both pbi_comm and pbi_name carry the mutable process *title*, which a node-based
    // agent (Claude Code) sets to its version string ("2.1.196"), not "claude".
    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    // sysctl({CTL_KERN, KERN_PROCARGS2, pid}) returns argc + the kernel's recorded
    // executable path + argv + env for a process. The exec path is immune to a process
    // changing its title, so it is the reliable name source for the agent ancestry walk.
    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int sysctl(int* name, uint namelen, byte* oldp, nuint* oldlenp, byte* newp, nuint newlen);

    const int CtlKern          = 1;
    const int KernProcArgs2    = 49;
    const int ProcPidTBsdInfo  = 3;
    const int ProcBsdInfoSize  = 136;
    const int ProcBsdInfoPpid  = 16;
    const int ProcBsdInfoName  = 64; // pbi_name[2*MAXCOMLEN] = 32 bytes

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetCurrentProcess();

    // Fills lpExeName with the full path of the process image (e.g. "C:\...\claude.exe").
    // On input lpdwSize is the buffer size in chars; on success it's set to the number of
    // chars written (excluding the terminating null). dwFlags 0 = Win32 path format.
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint                          processHandle,
        int                           processInformationClass,
        ref ProcessBasicInformation   processInformation,
        int                           processInformationLength,
        out int                       returnLength
    );

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation {
        public  nint  ExitStatus;
        public  nint  PebBaseAddress;
        public  nint  AffinityMask;
        public  nint  BasePriority;
        public  nuint UniqueProcessId;
        public  nuint InheritedFromUniqueProcessId;
    }

    const uint SYNCHRONIZE   = 0x00100000;
    const uint WAIT_TIMEOUT  = 258;

    // QueryFullProcessImageName sets this when lpExeName is too small for the image path.
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    // Minimal access right that still permits NtQueryInformationProcess(ProcessBasicInformation)
    // and QueryFullProcessImageName on Vista+. Cheaper than PROCESS_QUERY_INFORMATION and
    // succeeds for same-user processes even when they're protected/elevated-adjacent.
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // GetStdHandle ids and the SetHandleInformation inherit flag.
    const int  STD_INPUT_HANDLE   = -10;
    const int  STD_OUTPUT_HANDLE  = -11;
    const int  STD_ERROR_HANDLE   = -12;
    const uint HANDLE_FLAG_INHERIT = 0x00000001;

    /// <summary>
    /// Returns the parent process id of the current process, or <c>null</c> on failure.
    /// </summary>
    public static int? GetParentPid() {
        try {
            return OperatingSystem.IsWindows() ? GetParentPidWindows() : getppid_native();
        } catch {
            return null;
        }
    }

    static int? GetParentPidWindows() {
        var handle = GetCurrentProcess();
        var pbi    = default(ProcessBasicInformation);
        var status = NtQueryInformationProcess(handle, 0, ref pbi, Unsafe.SizeOf<ProcessBasicInformation>(), out _);

        return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : null;
    }

    /// <summary>
    /// Clears <c>HANDLE_FLAG_INHERIT</c> on this process's standard input/output/error
    /// handles (Windows only) so that child processes started afterwards do NOT inherit
    /// them. Call this immediately before spawning a long-lived detached process such as
    /// the transcript watcher.
    /// </summary>
    /// <remarks>
    /// Background (AI-820): hooks are invoked by the coding agent (Claude/Codex) with their
    /// stdio wired to pipes the agent reads. .NET's <see cref="System.Diagnostics.Process"/>
    /// always passes <c>bInheritHandles: true</c> to <c>CreateProcess</c> when any stream is
    /// redirected, so a watcher spawned from inside a hook inherits the hook process's own
    /// std handles — i.e. the agent's pipe write-ends. Closing the watcher's redirected
    /// streams on the parent side does nothing about those inherited copies, so the watcher
    /// holds the agent's pipe open for its entire (long) lifetime. The agent's read of the
    /// hook's stdout therefore never reaches EOF: a synchronous <c>SubagentStart</c> hook
    /// stalls until its timeout, surfacing as <c>[Tool result missing due to internal error]</c>
    /// from the Agent/Task tool, and the watcher is then orphaned because the disrupted flow
    /// never fires the matching <c>SubagentStop</c> that would reap it.
    ///
    /// Unix is unaffected: <c>fork</c>+<c>exec</c> <c>dup2</c>s the redirect pipes over fds
    /// 0/1/2 in the child and the agent's original pipe fds are not retained, so there is no
    /// inherited copy to leak. Hence this is a no-op off Windows.
    ///
    /// Clearing the flag does not close the handles or stop the hook from writing its own
    /// output to the agent — it only prevents subsequently-spawned children from inheriting
    /// them.
    /// </remarks>
    public static void PreventInheritedStdHandles() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        ClearStdHandleInherit(STD_INPUT_HANDLE,  "stdin");
        ClearStdHandleInherit(STD_OUTPUT_HANDLE, "stdout");
        ClearStdHandleInherit(STD_ERROR_HANDLE,  "stderr");
    }

    static bool stdHandleInheritWarned;

    static void ClearStdHandleInherit(int stdHandleId, string streamName) {
        try {
            var handle = GetStdHandle(stdHandleId);

            // No handle for this stream (NULL/INVALID) — nothing to clear, not a failure.
            if (handle == 0 || handle == -1) {
                return;
            }

            if (TryClearInheritFlag(handle)) {
                return;
            }

            // SetHandleInformation genuinely failed on a valid handle: the AI-820
            // mitigation did not apply, so a spawned watcher may still inherit this
            // pipe and reintroduce the hang/leak. Surface one diagnostic per process
            // (don't spam the agent's hook output) and never throw.
            if (!stdHandleInheritWarned) {
                stdHandleInheritWarned = true;
                Console.Error.WriteLine(
                    $"[kcap] warning: could not clear HANDLE_FLAG_INHERIT on {streamName} "
                  + $"(win32 error {Marshal.GetLastPInvokeError()}); a spawned watcher may inherit std handles (AI-820).");
            }
        } catch {
            // Best effort — handle hygiene must never crash the spawn path.
        }
    }

    /// <summary>
    /// Clears <c>HANDLE_FLAG_INHERIT</c> on a single Windows handle so processes spawned
    /// afterwards won't inherit it. Returns <c>true</c> on success, and (off Windows) as a
    /// defined no-op. This seam exists so the inherit-clearing behaviour used by
    /// <see cref="PreventInheritedStdHandles"/> can be tested against an arbitrary handle
    /// without mutating the test host's own standard handles.
    /// </summary>
    internal static bool TryClearInheritFlag(nint handle) {
        if (!OperatingSystem.IsWindows()) {
            return true;
        }

        // GetStdHandle returns NULL (0) when the stream has no handle and
        // INVALID_HANDLE_VALUE (-1) on error; SetHandleInformation fails harmlessly on
        // either, so reject them up front rather than make a doomed call.
        if (handle == 0 || handle == -1) {
            return false;
        }

        return SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0);
    }

    /// <summary>
    /// Returns the PID of the long-lived coding-agent process (codex/claude) that
    /// owns this hook invocation, suitable for passing to a spawned watcher via
    /// <c>--parent-pid</c>.
    /// </summary>
    /// <remarks>
    /// On Unix, returns the process group id (<c>getpgrp</c>). Both Codex and
    /// Claude run as process group leaders (PGID == PID), and any descendant —
    /// including the hook process — inherits that PGID through fork/exec. Using
    /// PGID bypasses the short-lived hook-executor process that <c>getppid</c>
    /// would point to and that dies the moment the hook returns, which is the
    /// bug that left Codex sessions stuck "active" because the watcher's
    /// <c>IsProcessAlive(getppid())</c> check fired against an already-dead PID
    /// at startup.
    ///
    /// Defensive: if PGID equals our own PID (we're somehow the group leader)
    /// the fallback is <c>getppid</c> — monitoring ourselves would let the
    /// watcher self-terminate immediately.
    ///
    /// On Windows there's no process-group equivalent, so the vendor-aware overload
    /// walks the parent-PID ancestry by process name instead (see
    /// <see cref="GetCodingAgentPid(string?)"/>); this parameterless overload, with no
    /// vendor to match, falls back to the immediate parent PID.
    /// </remarks>
    public static int? GetCodingAgentPid() => GetCodingAgentPid(vendor: null);

    /// <summary>
    /// Resolves the PID of the long-lived coding-agent process for
    /// <paramref name="vendor"/> (claude/codex/copilot, or a future one), suitable for
    /// the watcher's parent-PID watchdog. On Unix it first walks the ppid ancestry
    /// looking for the agent by name (<see cref="ResolveCodingAgentPid"/> +
    /// <see cref="GetProcessInfo"/>), which is robust to whatever process-group/launcher
    /// topology the agent uses to spawn its hook — notably Claude, whose hook runs in a
    /// separate transient process group that makes the bare <c>getpgrp()</c> heuristic
    /// resolve an already-dead PID. It falls back to that legacy heuristic when no agent
    /// is found on the chain (or <paramref name="vendor"/> is null/empty), preserving
    /// prior behaviour for Codex and any vendor whose process isn't named after it.
    /// </summary>
    public static int? GetCodingAgentPid(string? vendor) {
        if (OperatingSystem.IsWindows()) {
            // Walk the ppid ancestry by process name to skip the transient per-hook
            // executor. GetParentPidWindows() alone returns that executor, which has
            // usually already exited by the time the watcher boots — so the parent-PID
            // watchdog saw a dead PID at startup and silently never armed (AI-822).
            // Falls back to the immediate parent when no agent is found on the chain
            // (preserving prior behaviour for the no-vendor / unmatched cases). PID reuse
            // is a known Windows hazard for ppid walks, but the by-name match means a
            // recycled PID would have to *be* the agent's executable to falsely match.
            var startPid = GetParentPidWindows();

            if (!string.IsNullOrEmpty(vendor)
             && startPid is { } sp
             && ResolveCodingAgentPid(sp, vendor, GetProcessInfo) is { } winAgentPid) {
                return winAgentPid;
            }

            return startPid;
        }

        // Walk the ancestry for any named vendor. The match is by the vendor's process
        // name and falls back to the legacy heuristic below when no agent is found, so
        // this can't regress a vendor whose process isn't named after it (e.g. an npm
        // shim) — it only adds coverage where the name matches.
        if (!string.IsNullOrEmpty(vendor)
         && ResolveCodingAgentPid(getppid_native(), vendor, GetProcessInfo) is { } agentPid) {
            return agentPid;
        }

        // Fall back to getppid for the degenerate cases: pgid <= 1 means no
        // group / init, and pgid == our own pid means we're our own group
        // leader — monitoring ourselves would let the watcher self-terminate.
        var pgid = getpgrp_native();

        return pgid > 1 && pgid != getpid_native() ? pgid : getppid_native();
    }

    /// <summary>
    /// Walks the parent-process chain from <paramref name="startPid"/> and returns
    /// the PID of the nearest ancestor whose executable name identifies the coding
    /// agent for <paramref name="vendor"/> ("claude"/"codex"), or null if none is
    /// found within <paramref name="maxHops"/>. Pure: the process table is supplied
    /// via <paramref name="lookup"/> (pid → (ppid, comm)) so it is unit-testable
    /// with synthetic ancestries and shared across platforms.
    /// </summary>
    /// <remarks>
    /// The durable coding-agent process always sits on the ppid chain above the
    /// short-lived hook/launcher process, whatever the process-group topology —
    /// which is why the chain walk is reliable where <c>getpgrp()</c>/<c>getppid()</c>
    /// alone are not. Claude in particular spawns its hook in a separate, transient
    /// process group, so <c>getpgrp()</c> resolved a PID that was already dead by the
    /// time the watcher checked it, and the parent-PID watchdog silently never started.
    /// </remarks>
    internal static int? ResolveCodingAgentPid(
            int                                 startPid,
            string                              vendor,
            Func<int, (int ppid, string comm)?> lookup,
            int                                 maxHops = 16
        ) {
        var pid = startPid;

        for (var hop = 0; hop < maxHops && pid > 1; hop++) {
            if (lookup(pid) is not { } info) {
                return null;
            }

            if (MatchesAgentName(info.comm, vendor)) {
                return pid;
            }

            pid = info.ppid;
        }

        return null;
    }

    /// <summary>
    /// Returns <c>(ppid, comm)</c> for an arbitrary live PID, or null if the process
    /// can't be inspected (gone, access denied, or unsupported platform). Feeds the
    /// ancestry walk in <see cref="ResolveCodingAgentPid"/> on every platform — the
    /// Windows implementation (AI-822) reads the parent PID via
    /// <c>NtQueryInformationProcess</c> and the image name via
    /// <c>QueryFullProcessImageName</c>.
    /// </summary>
    public static (int ppid, string comm)? GetProcessInfo(int pid) {
        if (pid <= 0) {
            return null;
        }

        if (OperatingSystem.IsWindows()) {
            return GetProcessInfoWindows(pid);
        }

        return OperatingSystem.IsMacOS() ? GetProcessInfoMac(pid) : GetProcessInfoLinux(pid);
    }

    static unsafe (int ppid, string comm)? GetProcessInfoWindows(int pid) {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);

        if (handle == 0) {
            return null;
        }

        try {
            var pbi    = default(ProcessBasicInformation);
            var status = NtQueryInformationProcess(handle, 0, ref pbi, Unsafe.SizeOf<ProcessBasicInformation>(), out _);

            if (status != 0) {
                return null;
            }

            var ppid = (int)pbi.InheritedFromUniqueProcessId;

            // comm is the full image path; MatchesAgentName takes the basename. An empty
            // string on failure still lets the ancestry walk continue past this node (it
            // just won't match here) — but if it's the agent's own path that fails to read,
            // resolution falls back to the immediate parent and the watchdog can mis-arm,
            // so QueryImageName grows the buffer rather than giving up on long paths.
            return (ppid, QueryImageName(handle) ?? "");
        } finally {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads a process's full image path via <c>QueryFullProcessImageName</c>, retrying on a
    /// heap buffer if the path exceeds the initial stack buffer (Windows long paths can reach
    /// ~32K chars). Returns null if the call fails for any reason other than buffer size.
    /// </summary>
    static unsafe string? QueryImageName(nint handle) {
        const int stackCap = 1024;
        char*     stackBuf = stackalloc char[stackCap];
        var       len      = (uint)stackCap;

        if (QueryFullProcessImageName(handle, 0, stackBuf, ref len)) {
            return new string(stackBuf, 0, (int)len);
        }

        if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER) {
            return null;
        }

        // Extended-length path maximum (\\?\-prefixed paths). One retry covers every
        // realistic case; too large for stackalloc, so use a heap buffer.
        const int maxCap   = 32768;
        var       heapBuf  = new char[maxCap];

        fixed (char* p = heapBuf) {
            len = maxCap;

            return QueryFullProcessImageName(handle, 0, p, ref len) ? new string(p, 0, (int)len) : null;
        }
    }

    static unsafe (int ppid, string comm)? GetProcessInfoMac(int pid) {
        Span<byte> buf = stackalloc byte[ProcBsdInfoSize];
        buf.Clear();

        int written;

        fixed (byte* p = buf) {
            written = proc_pidinfo(pid, ProcPidTBsdInfo, 0, p, ProcBsdInfoSize);
        }

        // proc_pidinfo returns the number of bytes written; anything short of the
        // full struct means the call failed (e.g. no such process).
        if (written < ProcBsdInfoSize) {
            return null;
        }

        var ppid = BitConverter.ToInt32(buf.Slice(ProcBsdInfoPpid, sizeof(int)));

        // Take the name from the executable path, NOT proc_bsdinfo's name fields: a
        // node-based agent (Claude Code) calls setproctitle with its version string,
        // which overwrites BOTH pbi_comm and pbi_name (they read "2.1.196", never
        // "claude"), so the ancestry walk would miss the durable agent and the
        // parent-PID watchdog would fall back to the transient hook process-group
        // leader — killing the watcher mid-session. The kernel's recorded exec path
        // is unaffected by a title change. Fall back to pbi_name only when the exec
        // path is unreadable (e.g. a zombie or a permission-restricted process).
        var comm = ReadExecPathMac(pid);

        if (string.IsNullOrEmpty(comm)) {
            var nameSpan = buf.Slice(ProcBsdInfoName, 32);
            var nul      = nameSpan.IndexOf((byte)0);
            comm = Encoding.UTF8.GetString(nul >= 0 ? nameSpan[..nul] : nameSpan);
        }

        return (ppid, comm);
    }

    /// <summary>
    /// Returns the executable path of <paramref name="pid"/> via the
    /// <c>KERN_PROCARGS2</c> sysctl — the same source <c>ps</c> uses — or null if it
    /// can't be read. Unlike <c>proc_bsdinfo</c>'s name fields, the exec path is the
    /// kernel's recorded image path and is not affected by a process changing its
    /// title, so it reliably identifies the coding agent for the ancestry walk.
    /// </summary>
    static unsafe string? ReadExecPathMac(int pid) {
        Span<int> mib = [CtlKern, KernProcArgs2, pid];
        nuint     size = 0;

        fixed (int* m = mib) {
            // First call (oldp = null) reports the buffer size to allocate. The buffer
            // holds argc + exec_path + argv + env, so it can't be guessed up front.
            if (sysctl(m, 3, null, &size, null, 0) != 0 || size == 0) {
                return null;
            }

            var buf = new byte[size];

            fixed (byte* b = buf) {
                // Reuse the same `size` as the buffer length; a short buffer would make
                // sysctl fail with ENOMEM rather than truncate, so we'd just fall back.
                if (sysctl(m, 3, b, &size, null, 0) != 0) {
                    return null;
                }
            }

            return ParseExecPath(buf.AsSpan(0, (int)size));
        }
    }

    /// <summary>
    /// Extracts the executable path from a <c>KERN_PROCARGS2</c> buffer: a 4-byte
    /// <c>argc</c> followed by the NUL-terminated exec path string. Pure so the
    /// parsing is unit-testable with a synthetic buffer. Returns null when the buffer
    /// is too short to contain the path or the path is empty.
    /// </summary>
    internal static string? ParseExecPath(ReadOnlySpan<byte> procArgs2) {
        if (procArgs2.Length <= sizeof(int)) {
            return null;
        }

        var rest = procArgs2[sizeof(int)..]; // skip the leading argc
        var nul  = rest.IndexOf((byte)0);
        var path = nul >= 0 ? rest[..nul] : rest;

        return path.IsEmpty ? null : Encoding.UTF8.GetString(path);
    }

    static (int ppid, string comm)? GetProcessInfoLinux(int pid) {
        string stat;

        try {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        } catch {
            return null;
        }

        // Format: "pid (comm) state ppid ...". comm can contain spaces and parens,
        // so anchor on the LAST ')': name is between the first '(' and last ')',
        // and ppid is the 2nd whitespace field after it (state is the 1st).
        var open  = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');

        if (open < 0 || close <= open) {
            return null;
        }

        var statComm = stat.Substring(open + 1, close - open - 1);
        var rest     = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (rest.Length < 2 || !int.TryParse(rest[1], out var ppid)) {
            return null;
        }

        // Prefer the executable basename from /proc/<pid>/exe over the stat comm for the
        // same reason as macOS (see GetProcessInfoMac): node's process.title support
        // prctl(PR_SET_NAME)s the stat comm to the agent's version string, so a node-
        // based agent's stat comm reads "2.1.196" rather than "claude". The /exe symlink
        // is maintained by the kernel and a title change can't touch it. Falls back to
        // the stat comm when the link is unreadable (kernel threads, permissions), so
        // this can only improve resolution, never regress it. Note: when the agent is
        // launched as `node <script>` (rather than a packaged binary) the link resolves
        // to "node"; that case still relies on the legacy fallback.
        string comm = statComm;

        try {
            if (File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.Name is { Length: > 0 } exeName) {
                comm = exeName;
            }
        } catch {
            // Best effort — keep the stat comm.
        }

        return (ppid, comm);
    }

    /// <summary>
    /// True when <paramref name="comm"/> names the coding-agent executable for
    /// <paramref name="vendor"/>. Matches the basename exactly (optionally plus a
    /// single file extension like <c>.exe</c>), case-insensitively — never a loose
    /// substring, so descriptive names such as the Electron desktop "Claude Helper
    /// (Renderer)" don't masquerade as the <c>claude</c> CLI.
    /// </summary>
    static bool MatchesAgentName(string comm, string vendor) {
        if (string.IsNullOrEmpty(comm)) {
            return false;
        }

        var slash    = comm.LastIndexOfAny(['/', '\\']);
        var basename = slash >= 0 ? comm[(slash + 1)..] : comm;

        // Reduce the basename to a clean stem: the whole name if it has no dot, or the part before a
        // single trailing extension (".exe"). Anything else (multiple dots, a space) is not a clean
        // executable name and never matches.
        var dot = basename.IndexOf('.');
        string stem;

        if (dot < 0) {
            stem = basename;
        } else if (dot > 0 && !basename.AsSpan(dot + 1).Contains('.') && !basename.AsSpan(dot + 1).Contains(' ')) {
            stem = basename[..dot];
        } else {
            return false;
        }

        // Match the vendor token OR `{vendor}-cli`. The bounded `-cli` tolerance fixes the Kiro
        // watchdog, whose durable process image is `kiro-cli` while its vendor token is `kiro`
        // (AI-1359); the clean-stem gate above keeps it from over-matching unrelated processes.
        return stem.Equals(vendor, StringComparison.OrdinalIgnoreCase)
            || stem.Equals($"{vendor}-cli", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detaches the current process from its controlling terminal on Unix so that
    /// closing the parent terminal does not deliver SIGHUP to this process.
    /// </summary>
    /// <remarks>
    /// The watcher is forked by the coding-agent's hook executor and inherits the
    /// agent's process group and controlling terminal. When the user closes the
    /// terminal, the kernel sends SIGHUP to the controlling-terminal's foreground
    /// process group — that includes the watcher, which has no SIGHUP handler and
    /// dies instantly with default action <c>terminate</c>. The parent-PID poll in
    /// <c>WatchCommand.RunWatch</c> never gets to fire, so the watcher never
    /// POSTs session-end and the session stays "active" forever in the read model.
    ///
    /// <c>setsid()</c> moves the watcher into a new session with no controlling
    /// terminal. The captured <c>parentPid</c> (the coding-agent's PGID, recorded
    /// before the watcher was spawned) is unchanged and still resolves to the
    /// agent's actual PID — when the agent dies the 5s polling loop detects it
    /// and runs the cleanup path that POSTs session-end.
    ///
    /// No-op on Windows (no equivalent terminal-session abstraction; closing a
    /// console window kills the watcher via a different mechanism and the
    /// codex/Windows combination is uncommon).
    /// </remarks>
    /// <returns>
    /// <c>true</c> if the process was successfully detached (or if running on
    /// Windows where the call is intentionally a no-op). <c>false</c> if
    /// <c>setsid()</c> failed — typically because the process is already a
    /// process-group leader, which means it's already isolated and the call
    /// would be redundant anyway.
    /// </returns>
    public static bool DetachFromControllingTerminal() {
        if (OperatingSystem.IsWindows()) {
            return true;
        }

        // setsid() fails with EPERM if the caller is already a process-group
        // leader. That's fine — it means we're already isolated. Any other
        // failure is logged by the caller but isn't fatal: SIGHUP delivery may
        // kill the watcher in that edge case, but the worst outcome is the
        // pre-existing bug, not a regression.
        return setsid_native() != -1;
    }

    /// <summary>
    /// Returns true if a process with the given id exists at the moment of the call.
    /// PID reuse is theoretically possible but unlikely within a 5-second polling window.
    /// </summary>
    public static bool IsProcessAlive(int pid) {
        if (pid <= 1) {
            return false;
        }

        return OperatingSystem.IsWindows() ? IsProcessAliveWindows(pid) : IsProcessAliveUnix(pid);
    }

    static bool IsProcessAliveUnix(int pid) {
        var rc = kill_native(pid, 0);

        if (rc == 0) {
            return true;
        }

        // Distinguish "no such process" (dead) from "permission denied" (alive but not signallable).
        return Marshal.GetLastPInvokeError() == EPERM;
    }

    static bool IsProcessAliveWindows(int pid) {
        var handle = OpenProcess(SYNCHRONIZE, false, (uint)pid);

        if (handle == 0) {
            return false;
        }

        try {
            // WAIT_OBJECT_0 (0) = signalled => process has exited.
            // WAIT_TIMEOUT       => still running.
            return WaitForSingleObject(handle, 0) == WAIT_TIMEOUT;
        } finally {
            CloseHandle(handle);
        }
    }
}
