using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Pty.Windows;

// A tiny, disposable process the OUTER test can kill and observe from the outside — the
// mechanism the PDEATHSIG and spawner-thread-FailFast tests need (you cannot safely assert
// "my own process just crashed" from inside the process doing the crashing).
var mode = args.Length > 0 ? args[0] : "";

switch (mode) {
    case "spawn-dummy": {
        // Exercises the REAL production entry point end-to-end (not a shortcut into pty_spawn
        // directly) so this proves the actual daemon spawn path, including the spawner thread
        // Task 5 wires UnixPtyProcessFactory through.
        // No disposal of the spawner thread here — this whole process is a disposable one-shot
        // the outer test kills (SIGKILL) to observe PDEATHSIG, so a graceful Dispose() never runs
        // and never needs to.
        var factory = new UnixPtyProcessFactory(new UnixSpawnerThread());
        var proc    = factory.Spawn("sleep", ["30"], Directory.GetCurrentDirectory());
        Console.WriteLine($"PID={proc.Pid}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // block until the outer test kills THIS process
        break;
    }
    case "crash-spawner": {
        // Forces the spawner thread's underlying loop to throw unexpectedly, exercising the
        // Environment.FailFast policy — the outer test asserts THIS process dies loudly rather
        // than lingering half-broken.
        var thread = new UnixSpawnerThread();
        thread.CrashForTest(); // test-only seam added in Step 3
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        break;
    }
    case "mac-identity-smoke": {
        // Runtime-capture smoke for the per-RID macOS packaging checks (design spec §4.3/§5,
        // L1-build): captures THIS process's OWN mac: identity (a direct pty_capture_mac_identity
        // call; we are not ourselves spawned via pty_spawn) and a spawned child's identity (read
        // back from IPtyProcess.StartIdentity, which pty_spawn already captures natively at spawn
        // time -- the SAME production capture path, not a re-implementation). Both must be
        // present (non-empty) and distinct: proves the real kernel primitive is available and
        // actually varies per-process on this RID, not a mock or a hardcoded stub. Distinct from
        // Task 7's ProcessStartToken.ForPid mac: scheme tests, which exercise the separate C#
        // duplication (see pty_shim.c's design note on why the capture exists in both places).
        if (!OperatingSystem.IsMacOS()) {
            Console.Error.WriteLine("mac-identity-smoke is macOS-only");
            return 1;
        }

        var self = CaptureOwnMacIdentity();
        if (string.IsNullOrEmpty(self)) {
            Console.Error.WriteLine("FAIL: could not capture this process's own mac identity");
            return 1;
        }

        using var spawner = new UnixSpawnerThread();
        var factory       = new UnixPtyProcessFactory(spawner);
        var child         = factory.Spawn("sleep", ["5"], Directory.GetCurrentDirectory());
        try {
            var childIdentity = child.StartIdentity;
            if (string.IsNullOrEmpty(childIdentity)) {
                Console.Error.WriteLine("FAIL: could not capture spawned child's mac identity");
                return 1;
            }
            if (childIdentity == self) {
                Console.Error.WriteLine($"FAIL: self and child mac identities are identical ({self})");
                return 1;
            }
            Console.WriteLine($"SELF={self}");
            Console.WriteLine($"CHILD={childIdentity}");
        } finally {
            await child.DisposeAsync();
        }
        break;
    }
    case "win-ui-restricted-fail-closed": {
        // Windows-only: proves ConPtyProcess.Spawn fails CLOSED when the daemon process is
        // already inside a UI-restricted job (the classic job-nesting blocker). This MUST run
        // out-of-process: assigning a process to a UI-restricted job is IRREVERSIBLE (Windows
        // has no un-assign API), so doing it in the shared test host would permanently poison
        // every later job-NESTING test in that process. This whole process is a disposable
        // one-shot the outer test launches purely to read its exit code. Exit codes:
        //   0  = failed CLOSED (Spawn threw, no uncontained child was created) — the GOOD case
        //   20 = LEAKED (Spawn returned a live child despite the UI-restricted job)
        //   30 = could not even set up the poison job (inconclusive / environment error)
        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("win-ui-restricted-fail-closed is Windows-only");
            return 1;
        }

        return WinUiRestrictedFailClosed.Run();
    }
    default:
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 1;
}

return 0;

// Direct P/Invoke to the shim's exported pty_capture_mac_identity (see UnixPtyInterop) for
// THIS process's own pid. A spawned child's identity comes back for free via
// IPtyProcess.StartIdentity above; there's no equivalent "ambient" capture for the process
// that's doing the spawning, so this is the one place this smoke calls the native export
// directly rather than going through the production spawn path.
static unsafe string? CaptureOwnMacIdentity() {
    Span<byte> buf = stackalloc byte[128];
    fixed (byte* p = buf) {
        if (UnixPtyInterop.pty_capture_mac_identity(Environment.ProcessId, p, (nuint)buf.Length) == 0) {
            return null;
        }
        var len = 0;
        while (len < buf.Length && p[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(p, len);
    }
}

// Out-of-process body for the "win-ui-restricted-fail-closed" mode. Kept Windows-guarded so the
// kernel32 P/Invokes and the ConPtyProcess.Spawn call are only reached on Windows; the whole
// class still COMPILES on macOS/Linux (the mode's caller early-returns off-Windows, and none of
// these bodies run there).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class WinUiRestrictedFailClosed {
    internal static int Run() {
        IntPtr job;

        try {
            job = CreateUiRestrictedJobAndAssignSelf();
        } catch (Exception ex) {
            Console.Error.WriteLine($"SETUP-FAIL: {ex.Message}");

            return 30; // could not establish the poison job — inconclusive, not a leak
        }

        try {
            ConPtyProcess proc;

            try {
                // A UI-restricted outer job blocks a nested job from forming, so CreateProcessW
                // (with the PROC_THREAD_ATTRIBUTE_JOB_LIST attribute) fails and Spawn throws
                // BEFORE any child exists — the fail-closed guarantee under test.
                proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "exit"], Directory.GetCurrentDirectory());
            } catch (Exception ex) {
                Console.WriteLine($"FAIL-CLOSED: {ex.Message}");

                return 0; // Spawn threw, no uncontained child — correct
            }

            // Reached only if Spawn returned a live process despite the UI-restricted job — an
            // uncontained child leaked. Kill it via its own killing job before reporting.
            Console.Error.WriteLine($"LEAKED: uncontained child pid {proc.Pid}");

            try { proc.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* already exiting */ }

            return 20;
        } finally {
            ConPtyInterop.CloseHandle(job);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetCurrentProcess();

    // JobObjectBasicUIRestrictions (JOBOBJECTINFOCLASS = 4) with JOB_OBJECT_UILIMIT_HANDLES is the
    // one job-limit family that genuinely blocks job nesting (it depends on desktop/window-station
    // isolation, which conflicts with a nested job's parent already owning it). Mirrors
    // ConPtyJobObjectTestHelper.CreateUiRestrictedJobAndAssignSelf — duplicated here because that
    // helper lives in the test assembly, which this NativeTestHost does not reference.
    const int  JobObjectBasicUIRestrictions = 4;
    const uint JOB_OBJECT_UILIMIT_HANDLES   = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_UI_RESTRICTIONS {
        public uint UIRestrictionsClass;
    }

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObjectUi(
        IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS lpJobObjectInfo, uint cbJobObjectInfoLength);

    static IntPtr CreateUiRestrictedJobAndAssignSelf() {
        var job = ConPtyInterop.CreateJobObjectW(IntPtr.Zero, null);

        if (job == IntPtr.Zero) {
            throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
        }

        var uiRestrictions = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_HANDLES };

        if (!SetInformationJobObjectUi(
                job, JobObjectBasicUIRestrictions, ref uiRestrictions,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>())) {
            var err = Marshal.GetLastWin32Error();
            ConPtyInterop.CloseHandle(job);

            throw new InvalidOperationException($"SetInformationJobObject (UI restrictions) failed: {err}");
        }

        if (!ConPtyInterop.AssignProcessToJobObject(job, GetCurrentProcess())) {
            var err = Marshal.GetLastWin32Error();
            ConPtyInterop.CloseHandle(job);

            throw new InvalidOperationException($"AssignProcessToJobObject (self) failed: {err}");
        }

        return job;
    }
}
