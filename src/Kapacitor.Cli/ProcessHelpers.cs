using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kapacitor.Cli;

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

        var pgid = getpgrp_native();

        if (pgid <= 1) {
            return getppid_native();
        }

        // Avoid self-monitoring when this process happens to be its own group leader.
        return pgid == getpid_native() ? getppid_native() : pgid;
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
