using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Windows;
using static Capacitor.Cli.Daemon.Pty.Windows.ConPtyInterop;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Windows-only raw Win32 helpers backing <see cref="ConPtyJobObjectTests"/>. Every entry
/// point throws <see cref="PlatformNotSupportedException"/> off-Windows rather than being
/// <c>#if</c>-excluded — Capacitor.Cli.Tests.Unit is one cross-platform assembly (there is no
/// WINDOWS define in this csproj), so this file must still compile — though never execute its
/// bodies — on macOS/Linux. Declared with classic <c>[DllImport]</c> rather than
/// <c>[LibraryImport]</c>: the test project isn't AOT-published, so there's no reason to also
/// opt it into <c>AllowUnsafeBlocks</c> just for these test-only calls.
/// </summary>
internal static class ConPtyJobObjectTestHelper {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJobNative(IntPtr processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // JobObjectBasicUIRestrictions (JOBOBJECTINFOCLASS = 4): the one job-limit family that
    // genuinely blocks nesting — it depends on desktop/window-station isolation, which
    // conflicts with a nested job's parent already owning that isolation. Used only to
    // simulate "nesting genuinely prevented" for the fail-closed test.
    const int  JobObjectBasicUIRestrictions = 4;
    const uint JOB_OBJECT_UILIMIT_HANDLES   = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_UI_RESTRICTIONS {
        public uint UIRestrictionsClass;
    }

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObjectUiRestrictions(
        IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS lpJobObjectInfo, uint cbJobObjectInfoLength);

    /// <summary>Assigns the CURRENT (test) process into <paramref name="job"/>.</summary>
    internal static void AssignSelfToJob(IntPtr job) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        if (!AssignProcessToJobObject(job, GetCurrentProcess())) {
            throw new InvalidOperationException($"AssignProcessToJobObject (self) failed: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// Creates a job carrying a UI-restriction limit (the classic nesting blocker) and assigns
    /// the CURRENT process to it, so a subsequently spawned <see cref="ConPtyProcess"/> cannot
    /// nest its own job underneath — <c>Spawn</c> must then fail closed rather than launch
    /// uncontained.
    /// </summary>
    internal static IntPtr CreateUiRestrictedJobAndAssignSelf() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var job = CreateJobObjectW(IntPtr.Zero, null);

        if (job == IntPtr.Zero) {
            throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
        }

        var uiRestrictions = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_HANDLES };

        if (!SetInformationJobObjectUiRestrictions(
                job, JobObjectBasicUIRestrictions, ref uiRestrictions,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>())) {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(job);

            throw new InvalidOperationException($"SetInformationJobObject (UI restrictions) failed: {err}");
        }

        AssignSelfToJob(job);

        return job;
    }

    /// <summary>
    /// Joins the CURRENT process to <paramref name="jobHandle"/> (a production job created by
    /// <see cref="ConPtyProcess.Spawn"/>, which carries no breakaway-allowed flags), then
    /// attempts a raw CreateProcess with CREATE_BREAKAWAY_FROM_JOB. Returns true iff the create
    /// call SUCCEEDED (breakaway was allowed — the bad outcome); a correctly-contained job makes
    /// this return false.
    /// </summary>
    internal static bool TryCreateWithBreakaway(string fileName, string arguments, IntPtr jobHandle) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        AssignSelfToJob(jobHandle);

        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOW>();

        var created = CreateProcessW(
            null, $"{fileName} {arguments}", IntPtr.Zero, IntPtr.Zero, false,
            CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, null, ref si, out var pi);

        if (created) {
            TerminateProcess(pi.hProcess, 0);
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }

        return created;
    }

    /// <summary>
    /// True iff the process identified by <paramref name="pid"/> is (transitively) a member of
    /// <paramref name="job"/>, via the native <c>IsProcessInJob</c> Win32 call.
    /// </summary>
    internal static bool IsProcessInJob(int pid, IntPtr job) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

        if (hProcess == IntPtr.Zero) {
            throw new InvalidOperationException($"OpenProcess failed: {Marshal.GetLastWin32Error()}");
        }

        try {
            if (!IsProcessInJobNative(hProcess, job, out var result)) {
                throw new InvalidOperationException($"IsProcessInJob failed: {Marshal.GetLastWin32Error()}");
            }

            return result;
        } finally {
            CloseHandle(hProcess);
        }
    }
}

/// <summary>
/// Test-only reach-through to <see cref="ConPtyProcess"/>'s internal job handle — kept as a
/// separate tiny accessor (rather than exposing the handle to all of
/// <see cref="ConPtyJobObjectTestHelper"/>'s Win32 surface) so it's obvious at the call site
/// that a test is deliberately reaching into production internals.
/// </summary>
internal static class ConPtyInteropTestAccessor {
    internal static IntPtr JobHandle(ConPtyProcess proc) => proc.JobHandleForTests;
}
