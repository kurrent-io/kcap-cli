using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetCurrentProcess();

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
    /// On Windows there's no process-group equivalent so we fall back to the
    /// parent PID. Codex on Windows is uncommon and the Claude path is already
    /// covered by Claude's own SessionEnd hook.
    /// </remarks>
    public static int? GetCodingAgentPid() {
        if (OperatingSystem.IsWindows()) {
            return GetParentPidWindows();
        }

        // Fall back to getppid for the degenerate cases: pgid <= 1 means no
        // group / init, and pgid == our own pid means we're our own group
        // leader — monitoring ourselves would let the watcher self-terminate.
        var pgid = getpgrp_native();

        return pgid > 1 && pgid != getpid_native() ? pgid : getppid_native();
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
