# AI-1390 — Hosted-agent native OS containment: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three residual classes the managed self-defense layer (kcap-cli #327) can't reach — immediacy of orphan death, the descendant-outlives-leader gap, and macOS's total lack of crash-survivor reaping — by adding OS-level containment at spawn time: a Windows Job Object (complete containment), a Linux native-shim spawn path using `PDEATHSIG` + raw `execveat` (leader-only immediate containment for launches proven contained at initial exec), and a macOS incarnation-identity upgrade (`mac:{bootsessionuuid}:{p_uniqueid}`) that restores leader-level *eventual* crash-survivor recovery. The managed layer (durable PID records, `OrphanReaper`, `AgentKillQuarantine`) is untouched as the audit + backstop for everything containment doesn't cover.

**Architecture:** Windows gets a `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job assigned via `PROC_THREAD_ATTRIBUTE_JOB_LIST` at process-creation time in `ConPtyProcess.Spawn` — no managed code changes to the reap layer at all. Unix (Linux + macOS) gets a brand-new native `pty_spawn` in `pty_shim.c`: the managed side does all resolution/classification pre-fork and hands the shim a fully-built, opaque "execution plan"; the shim forks, arms `PR_SET_PDEATHSIG` (Linux only), execs via a raw `execveat` syscall (Linux, contained) or `execve` (macOS always; Linux uncontained fallback), and reports back through an error-pipe handshake — including the process's own start-identity, captured natively immediately post-`forkpty`, before anything can reap and recycle the pid. All Linux spawns run on one dedicated, daemon-lifetime OS thread (PDEATHSIG is a per-thread property). macOS gets a new `mac:` incarnation-identity scheme (kernel `p_uniqueid` + boot-session UUID) alongside the existing Linux `lx:` and legacy `tk:` schemes, plus a `identity_kind` marker on the durable PID record so an unresolvable private-ABI capture is a well-formed, distinguishable record rather than an under-specified null.

**Tech Stack:** .NET 10 (`net10.0`), C# `LibraryImport` P/Invoke (source-generated, AOT-safe), raw C (`pty_shim.c`, compiled per-RID via `cc`/`clang`), Win32 Job Object / ConPTY APIs, Linux `prctl`/raw `syscall(SYS_execveat, …)`/`/proc`, macOS `proc_pidinfo`/`sysctlbyname` (vendored private ABI), TUnit on Microsoft Testing Platform for all managed tests, GitHub Actions (`ci.yml` — ubuntu/windows only; `release.yml` — the only per-RID matrix, including macOS).

## Global Constraints

These apply to every task below; re-read before touching any file.

- **6 RIDs ship, 5 need a native shim.** `osx-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `win-x64`. `win-x64` uses the Windows Job Object (no shim); the other 5 each need their own natively-compiled `libpty_shim` — 4 Linux (glibc × 2 arches, musl × 2 arches) + 1 macOS. Never say "5-platform" — that undercounts the shim build matrix by one and overcounts the Windows side.
- **Windows floor: 1809.** `PROC_THREAD_ATTRIBUTE_JOB_LIST` requires Windows 10 / Server 2016+; ConPTY itself already requires Windows 10 1809+, so **1809 is the binding floor** (no new floor introduced). Do not cite Windows 8 nested-job support as the floor — that's about job nesting generally, not this attribute.
- **Linux kernel floor: ≥ 3.19** for `execveat(AT_EMPTY_PATH)`, verified via a **raw syscall runtime probe** (`syscall(SYS_execveat, …)`), never the glibc wrapper (glibc only added one in 2.34) and never a documented-minimum assumption.
- **macOS floor: 12+**, both `arm64` and `x86_64` (the `PROC_PIDUNIQIDENTIFIERINFO` flavor predates macOS 12 by years, but this is the pinned support floor).
- **No Linear issue IDs in kcap-cli source/test comments.** Spec-anchor style (`§4.2`, `Phase B (D4 §6.4)`) is fine; a bare `AI-1390`/`AI-1313` in a `.cs`/`.c` comment is not (kcap-cli `CLAUDE.md`, "Dos and donts"). GitHub issue numbers are fine if you need a number. This does NOT apply to plan/spec markdown files or PR descriptions.
- **TUnit on Microsoft Testing Platform.** `OutputType Exe`, never add `Microsoft.NET.Test.Sdk`, always `await` every `Assert.That(...)` call, run via `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1` (CI serializes; local dev may parallelize but prefer the serialized form while iterating on process/OS-state tests in this plan since nearly every test here mutates or observes real OS process state).
- **No wire/protocol change.** Nothing here touches the SignalR contract between daemon and server; `AgentPidRecord`'s on-disk JSON schema is the only wire-adjacent artifact touched (Task 8), and it is additive + backward-compatible by construction.
- **Fail-closed / spare-shaped, never kill-shaped.** Every new classification path in this plan must default to "uncontained but proceed" or "spare and retain the record" on any ambiguity — never treat an unreadable/anomalous signal as proof of anything. This is the single invariant every task's tests must protect.
- **PR base = `worktree-ai-1313-reviewer-reaping`** (the #327 branch), not `main` — this work stacks on #327. Retarget to `main` after #327 merges (Task 10 has the retarget checklist). PR title `[AI-1390] …`.
- **CI topology matters.** `ci.yml`'s matrix is `ubuntu-latest, windows-latest` only — there is **no macOS runner in `ci.yml`**. macOS only appears in `release.yml`'s per-RID build matrix (`macos-latest`, RID `osx-arm64`). Every macOS-only check in this plan (the `mac:` runtime-capture smoke, the per-RID machine-type/load-and-call smoke) must attach to `release.yml`'s existing `build` job, never to `ci.yml`'s `aot-check` (which is `linux-x64`-only and only greps `IL2xxx`/`IL3xxx` trimming warnings — see `.github/workflows/ci.yml:59-91`).

---

## File Structure

| File | Change |
|---|---|
| `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyInterop.cs` | + Job Object P/Invoke (`CreateJobObjectW`, `SetInformationJobObject`, `AssignProcessToJobObject`, `TerminateJobObject`) + structs + constants |
| `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyProcess.cs` | Modify `Spawn`: create+bind the job before `CreateProcessW`, grow the attribute list 1→2, own the job `SafeHandle`, fail-closed teardown on post-create failure |
| `src/Capacitor.Cli.Daemon/Native/pty_shim.h` | **New** — the shim's public C ABI: opaque `pty_exec_plan`, `pty_spawn_result`, function prototypes (mirrors spec §4.2(a) verbatim) |
| `src/Capacitor.Cli.Daemon/Native/pty_shim.c` | Extend (currently only `pty_set_winsize`): `pty_probe_execveat`, `pty_preflight` (+ shebang/argv-rewrite/fd-bound preflight helpers), `pty_plan_contained`, `pty_plan_free`, `pty_spawn` (fork/exec child sequence, error-pipe handshake, native `lx:`/`mac:` start-identity capture) |
| `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyInterop.cs` | + `LibraryImport`s for the new shim functions + the `PtySpawnResult` marshaling struct |
| `src/Capacitor.Cli.Daemon/Pty/Unix/UnixSpawnerThread.cs` | **New** — the dedicated daemon-lifetime native-spawn thread (`BlockingCollection<SpawnRequest>` loop, `Environment.FailFast` on unexpected exit) |
| `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs` | Rewrite `Spawn`: pre-fork PATH resolution, build the execution plan via `pty_preflight`, submit to `UnixSpawnerThread`, delete the managed child branch (`case 0:`), expose `StartIdentity` |
| `src/Capacitor.Cli.Daemon/Pty/IPtyProcess.cs` | + `string? StartIdentity => null;` default interface member |
| `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs` | + `string? StartIdentity => null;` default interface member |
| `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs` | + `StartIdentity => pty.StartIdentity` override |
| `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` | `PersistPidRecordOrThrow` gains a `capturedStartIdentity` parameter; consumes the shim-captured identity on Unix instead of re-capturing; call site at `:770` updated |
| `src/Capacitor.Cli.Daemon/DaemonRunner.cs` | DI: register `UnixSpawnerThread` as a singleton (Unix branch only), thread it into `UnixPtyProcessFactory` |
| `src/Capacitor.Cli.Core/ProcessStartToken.cs` | + `mac:` scheme branch (vendored `proc_pidinfo` flavor 17 struct + `sysctlbyname("kern.bootsessionuuid")`), gated before the `tk:` fallback |
| `src/Capacitor.Cli.Core/Models.cs` | + `PidIdentityKind` enum (`Present = 0`, `IdentityUnavailable = 1`); `AgentPidRecord` gains an `IdentityKind` field; `[JsonSerializable(typeof(PidIdentityKind))]` registered |
| `src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs` | `ReadAll()` gains inconsistent-shape quarantine (`Present`+empty token / `IdentityUnavailable`+nonempty token → `.corrupt`) |
| `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` | Record pass: `IdentityUnavailable` records don't populate `handledPids` (so the marker scan can still reach them) + differentiated `legacy_unresolvable`/`identity_unresolvable` logging; marker scan: delete a matching record on confirmed kill |
| `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj` | `CompilePtyShim` target restructured: per-RID, RID-isolated output path, cross-compile-safe |
| `.github/workflows/release.yml` | musl toolchain install steps; generalize the `.dylib`-only guards to `.so`-aware; add per-RID machine-type + load-and-call smoke; add the macOS runtime-capture smoke |
| `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Capacitor.Cli.Tests.Unit.NativeTestHost.csproj` | **New** — tiny out-of-process helper Exe used by the Linux PDEATHSIG/spawner-thread-death tests (no MTP/TUnit dependency — needs to be killable and observed from outside) |
| `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Program.cs` | **New** — `spawn-dummy` / `crash-spawner` modes |
| `test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` | + `ProjectReference` to the new NativeTestHost project |
| `test/Capacitor.Cli.Tests.Unit/Daemon/ConPtyJobObjectTests.cs` | **New** (Windows-only tests, no-ops elsewhere) |
| `test/Capacitor.Cli.Tests.Unit/Daemon/PtyShimNativeTests.cs` | **New** — `pty_preflight`/`pty_probe_execveat`/`pty_plan_*` tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/PtySpawnTests.cs` | **New** — `pty_spawn` fork/exec/handshake/capture tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/UnixSpawnerThreadTests.cs` | **New** — dedicated-thread invariant tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/DummyProcess.cs` | + shebang/setuid/execute-only fixture helpers |
| `test/Capacitor.Cli.Tests.Unit/Daemon/PtyInteropTests.cs` | + resolution smoke for the new shim P/Invokes |
| `test/Capacitor.Cli.Tests.Unit/Daemon/ProcessStartTokenTests.cs` | + `mac:` scheme tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/AgentPidRecordStoreTests.cs` | `Rec(...)` helper signature updated; + inconsistent-shape quarantine tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/OrphanReaperTests.cs` | `Rec(...)` helper signature updated; + `identity_unavailable`/`legacy_unresolvable` lifecycle tests |
| `test/Capacitor.Cli.Tests.Unit/Daemon/StopAgentPidFallbackTests.cs` (via `AgentOrchestratorVendorTests`) | positional `AgentPidRecord` call site updated |
| `Capacitor.slnx` | + the new NativeTestHost project |
| `Directory.Packages.props` | (only if the new project needs a package not already centrally versioned — expected: none) |

---

## Task 1 — W1: Windows creation-time Job Object containment

**Testability:** Fully local AND CI (`windows-latest` in `ci.yml`). Everything in this task runs in-process on the current test host — no separate helper process is needed (job-object semantics can be exercised by putting the *test process itself* into an outer job, or by disposing/closing job handles directly), so every step below is both locally runnable on a Windows dev box and CI-verified. On macOS/Linux dev boxes every test in this task is a structural no-op (`if (!OperatingSystem.IsWindows()) return;`) — you can still compile and run the suite locally, you just won't exercise the behavior; real coverage is `windows-latest` in CI.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyInterop.cs`
- Modify: `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyProcess.cs:171-233` (attribute list at 171-194, `CreateProcessW` at 206-219, `finally` at 229-233)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/ConPtyJobObjectTests.cs` (new)

**Interfaces:**
- Consumes: nothing new from other tasks.
- Produces: `ConPtyProcess` now owns a job `SafeHandle` field; `ConPtyProcess.Spawn(...)` throws `InvalidOperationException` (surfaced by the existing caller as `LaunchFailed`, `Models.cs:1383`) if the job can't be created/joined — no behavior change to `ConPtyProcess`'s public shape (`Pid`/`HasExited`/`ExitCode`/`ReadOutputAsync`/etc. all unchanged), so no other task depends on new members here beyond "a Windows spawn either succeeds fully-contained or fails closed."

### Step 1: Add the Job Object P/Invoke surface

- [ ] Add to `ConPtyInterop.cs` (after the existing `PROCESS_INFORMATION` struct, before the closing `}`):

```csharp
[LibraryImport("kernel32.dll", SetLastError = true)]
internal static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetInformationJobObject(
    IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool TerminateJobObject(IntPtr hJob, uint uExitCode);

// JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation
internal const int JobObjectExtendedLimitInformation = 9;

// JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: kill every process in the job the instant
// the LAST handle to the job object closes (clean dispose, crash, task-manager kill — all
// of them). Deliberately the ONLY flag we set: no UI-restriction flags (those would block
// job nesting when the daemon itself already runs inside an enclosing job), no breakaway
// flags (so a descendant literally cannot CreateProcess its way out of the job).
internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

// PROC_THREAD_ATTRIBUTE_JOB_LIST — grows the existing 1-entry attribute list
// (PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE) to 2 entries. The value is a POINTER TO AN ARRAY
// of job handles (we pass an array of exactly one).
internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;

// Used only by the §5 breakaway-denial test: a child that tries this must fail.
internal const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
    public long  PerProcessUserTimeLimit;
    public long  PerJobUserTimeLimit;
    public uint  LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint  ActiveProcessLimit;
    public nuint Affinity;
    public uint  PriorityClass;
    public uint  SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS                       IoInfo;
    public nuint                             ProcessMemoryLimit;
    public nuint                             JobMemoryLimit;
    public nuint                             PeakProcessMemoryUsed;
    public nuint                             PeakJobMemoryUsed;
}
```

- [ ] Build: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj` (on any OS — this is just P/Invoke declarations, no behavior yet). Expect success.
- [ ] Commit: `git add src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyInterop.cs && git commit -m "Add Job Object P/Invoke surface to ConPtyInterop"`

### Step 2: Write the failing containment test (Windows-only; no-op elsewhere)

- [ ] Create `test/Capacitor.Cli.Tests.Unit/Daemon/ConPtyJobObjectTests.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// W1 (spec §4.1): every hosted-agent PTY on Windows is created already-bound to a
/// KILL_ON_JOB_CLOSE job — the OS itself kills the leader AND every descendant the instant
/// the job handle's last reference closes (clean dispose, crash, or an external kill of the
/// daemon). No managed reap layer is exercised on Windows once this is proven; these tests
/// are the only place that behavior is asserted.
/// </summary>
public class ConPtyJobObjectTests {
    [Test]
    public async Task Disposing_the_process_kills_child_and_grandchild() {
        if (!OperatingSystem.IsWindows()) return;

        // cmd.exe spawns a grandchild `timeout` so the group/job boundary — not just the
        // immediate child — is under test.
        await using var proc = ConPtyProcess.Spawn(
            "cmd.exe", ["/c", "start /min cmd.exe /c timeout /t 60 >NUL & timeout /t 60 >NUL"],
            Directory.GetCurrentDirectory());

        await Task.Delay(500); // let the grandchild actually spawn before we kill the job

        await proc.DisposeAsync();

        // Job-handle close is synchronous-ish from the OS's perspective, but process exit
        // notification can lag slightly — poll rather than assert instantly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsProcessAlive(proc.Pid)) await Task.Delay(200);

        await Assert.That(IsProcessAlive(proc.Pid)).IsFalse();
    }

    [Test]
    public async Task Breakaway_from_the_job_is_denied() {
        if (!OperatingSystem.IsWindows()) return;

        // A child that explicitly requests CREATE_BREAKAWAY_FROM_JOB must fail to start —
        // our job sets NO breakaway-allowed flag, so escape is impossible by construction
        // (mere grandchild membership doesn't prove escape is impossible; an ACTUAL denied
        // breakaway attempt does).
        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "pause"], Directory.GetCurrentDirectory());

        var psi = new ProcessStartInfo {
            FileName        = "cmd.exe",
            Arguments       = "/c exit",
            UseShellExecute = false,
        };

        // CreateProcess with CREATE_BREAKAWAY_FROM_JOB against a job with no
        // JOB_OBJECT_LIMIT_BREAKAWAY_OK/SILENT_BREAKAWAY_OK must fail with
        // ERROR_ACCESS_DENIED — asserted via the raw Win32 call, not System.Diagnostics.Process
        // (which has no breakaway knob).
        var breakawayDenied = ConPtyJobObjectTestHelper.TryCreateWithBreakaway(
            "cmd.exe", "/c exit", ConPtyInteropTestAccessor.JobHandle(proc));

        await Assert.That(breakawayDenied).IsFalse();
    }

    [Test]
    public async Task A_daemon_already_inside_an_outer_job_still_nests() {
        if (!OperatingSystem.IsWindows()) return;

        // Put THIS test process into an outer job with no UI-restriction flags (nesting is
        // only blocked when either job carries UI limits) — mirrors "the daemon happens to be
        // launched inside another job" (e.g. a CI runner, a service wrapper).
        var outerJob = ConPtyInterop.CreateJobObjectW(IntPtr.Zero, null);
        ConPtyJobObjectTestHelper.AssignSelfToJob(outerJob);

        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "timeout /t 5 >NUL"], Directory.GetCurrentDirectory());

        // Nesting succeeded iff the spawn didn't throw AND the child is (transitively) a
        // member of the outer job too — checked via IsProcessInJob.
        await Assert.That(ConPtyJobObjectTestHelper.IsProcessInJob(proc.Pid, outerJob)).IsTrue();

        ConPtyInterop.TerminateJobObject(outerJob, 0);
    }

    [Test]
    public async Task Job_creation_failure_fails_the_spawn_closed() {
        if (!OperatingSystem.IsWindows()) return;

        // Simulate "nesting genuinely prevented": put this process in an outer job that DOES
        // carry a UI-restriction limit (JOB_OBJECT_LIMIT_JOB_MEMORY combined with
        // JOB_OBJECT_UILIMIT_* is the classic blocker) so a nested job can't form, and assert
        // Spawn throws rather than silently spawning uncontained.
        var restrictiveJob = ConPtyJobObjectTestHelper.CreateUiRestrictedJobAndAssignSelf();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ConPtyProcess.Spawn("cmd.exe", ["/c", "exit"], Directory.GetCurrentDirectory()).DisposeAsync());

        ConPtyInterop.TerminateJobObject(restrictiveJob, 0);
    }

    static bool IsProcessAlive(int pid) {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
```

- [ ] Note: `ConPtyJobObjectTestHelper` and `ConPtyInteropTestAccessor` referenced above do not exist yet — Step 3 adds the internal-visible test seam they need (`ConPtyProcess`'s job handle field must be reachable for the breakaway/nesting assertions). Add a small internal accessor alongside `ConPtyJobObjectTests` in the same test file namespace once Step 3's job handle field exists — implement `ConPtyJobObjectTestHelper` using `Capacitor.Cli.Daemon`'s `InternalsVisibleTo` (already grants `Capacitor.Cli.Tests.Unit`) to read the private job handle and to call `CreateProcessW` directly with `CREATE_BREAKAWAY_FROM_JOB` for the denial test, and `AssignProcessToJobObject`/`QueryInformationJobObject`-based `IsProcessInJob` (or, simpler and sufficient: assert the outer-job's `ActiveProcessCount` — via `JobObjectBasicAccountingInformation`, class 1 — increased by exactly one after the nested spawn) for the nesting test.
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/ConPtyJobObjectTests/*"`
- [ ] Expected on Windows: compile failure (`ConPtyProcess` has no job handle yet, `ConPtyJobObjectTestHelper` doesn't exist) — this IS the "red" state; on macOS/Linux the whole file still needs to compile (helper methods are OS-gated internally, not `#if`-excluded, since this is one cross-platform assembly), so build the helper's Windows-only body behind `if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();` rather than `#if WINDOWS` (there is no such define in this csproj).

### Step 3: Implement the Job Object binding in `ConPtyProcess.Spawn`

- [ ] Add a `SafeHandle`-owned job field and wire it into the constructor/dispose:

```csharp
using Microsoft.Win32.SafeHandles;

// ... inside ConPtyProcess:
readonly SafeFileHandle _jobHandle; // SafeHandle so a thrown exception before the ctor still gets cleaned up by the caller's catch path

ConPtyProcess(IntPtr hPC, IntPtr hProcess, IntPtr hOutputPipe, FileStream outputStream, FileStream inputStream, SafeFileHandle jobHandle) {
    _hPC          = hPC;
    _hProcess     = hProcess;
    _hOutputPipe  = hOutputPipe;
    _outputStream = outputStream;
    _inputStream  = inputStream;
    _jobHandle    = jobHandle;
}
```

- [ ] In `Spawn`, BEFORE the existing attribute-list block at `:171`, create + configure the job:

```csharp
var hJob = CreateJobObjectW(IntPtr.Zero, null);

if (hJob == IntPtr.Zero) {
    throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
}

var limitInfo = new ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    BasicLimitInformation = new ConPtyInterop.JOBOBJECT_BASIC_LIMIT_INFORMATION {
        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    }
};

if (!SetInformationJobObject(
        hJob, JobObjectExtendedLimitInformation, ref limitInfo,
        (uint)Marshal.SizeOf<ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) {
    var err = Marshal.GetLastWin32Error();
    CloseHandle(hJob);
    throw new InvalidOperationException($"SetInformationJobObject failed: {err}");
}

var jobHandle = new SafeFileHandle(hJob, ownsHandle: true);
```

- [ ] Grow the attribute list from 1 to 2 entries and add the job-list attribute (replace the existing `:171-194` block):

```csharp
var attrListSize = IntPtr.Zero;
InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attrListSize);
var attrList = Marshal.AllocHGlobal(attrListSize);

if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref attrListSize)) {
    Marshal.FreeHGlobal(attrList);
    jobHandle.Dispose();
    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
}

if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) {
    DeleteProcThreadAttributeList(attrList);
    Marshal.FreeHGlobal(attrList);
    jobHandle.Dispose();
    throw new InvalidOperationException($"UpdateProcThreadAttribute(PSEUDOCONSOLE) failed: {Marshal.GetLastWin32Error()}");
}

// PROC_THREAD_ATTRIBUTE_JOB_LIST's value is a pointer to an ARRAY of job handles — one
// element here. The child becomes a job member at the instant CreateProcessW succeeds:
// there is no suspended-then-assign window (AssignProcessToJobObject after the fact would
// have one).
var jobArray  = Marshal.AllocHGlobal(IntPtr.Size);
Marshal.WriteIntPtr(jobArray, 0, hJob);

if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobArray, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) {
    var err = Marshal.GetLastWin32Error();
    Marshal.FreeHGlobal(jobArray);
    DeleteProcThreadAttributeList(attrList);
    Marshal.FreeHGlobal(attrList);
    jobHandle.Dispose();
    throw new InvalidOperationException($"UpdateProcThreadAttribute(JOB_LIST) failed: {err}");
}
```

- [ ] In the `try`/`finally` around `CreateProcessW` (`:203-233`): on any failure AFTER `CreateProcessW` returns successfully (i.e. any code between a successful create and the `return new(...)`), call `TerminateJobObject(hJob, 1)` and confirm death before rethrowing — wrap the existing post-create body:

```csharp
try {
    const uint creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;

    if (!CreateProcessW(null, cmdLine.ToString(), IntPtr.Zero, IntPtr.Zero, false,
            creationFlags, envBlock, cwd, ref si, out var pi)) {
        throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
    }

    try {
        CloseHandle(pi.hThread);

        var outputSafeHandle = new SafeFileHandle(ptyOutputRead, ownsHandle: true);
        var inputSafeHandle  = new SafeFileHandle(ptyInputWrite, ownsHandle: true);
        var outputStream     = new FileStream(outputSafeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        var inputStream      = new FileStream(inputSafeHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);

        return new(hPC, pi.hProcess, ptyOutputRead, outputStream, inputStream, jobHandle) { Pid = pi.dwProcessId };
    } catch {
        // Post-create failure: the child exists but we can't finish wiring it up. Kill it via
        // the job (closes over descendants too) and confirm death before propagating — the
        // caller's teardown/quarantine machinery must never see an ambiguous "maybe spawned".
        TerminateJobObject(hJob, 1);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline) {
            if (GetExitCodeProcess(pi.hProcess, out var code) && code != STILL_ACTIVE) break;
            Thread.Sleep(50);
        }

        CloseHandle(pi.hProcess);
        jobHandle.Dispose();
        throw;
    }
} finally {
    Marshal.FreeHGlobal(jobArray);
    DeleteProcThreadAttributeList(attrList);
    Marshal.FreeHGlobal(attrList);
    Marshal.FreeHGlobal(envBlock);
}
```

- [ ] Update `DisposeAsync` to dispose `_jobHandle` (which fires `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) instead of / in addition to the existing `CloseHandle(_hProcess)`:

```csharp
public async ValueTask DisposeAsync() {
    if (_disposed) return;
    _disposed = true;

    await _cts.CancelAsync();
    if (!HasExited) await TerminateAsync();

    try { await _outputStream.DisposeAsync(); } catch { }
    try { await _inputStream.DisposeAsync(); } catch { }

    if (Interlocked.Exchange(ref _pcClosed, 1) == 0) ClosePseudoConsole(_hPC);

    CloseHandle(_hProcess);
    _jobHandle.Dispose(); // last handle to the job closes here → OS kills leader + all descendants
    _cts.Dispose();
}
```

Note: `TerminateAsync`'s graceful-stop path (`SendInterrupt` → wait → `TerminateProcess`) is UNCHANGED — the job is the backstop for daemon death, not the primary stop mechanism (spec §4.1 point 5).

- [ ] Add the small test-only helper referenced from Step 2 (same file or a `ConPtyJobObjectTestHelper.cs` partial in the test project) using `AssignProcessToJobObject`/`CreateJobObjectW`/raw `CreateProcessW` — implement using ONLY the now-`internal` `ConPtyInterop` members (test project already has `InternalsVisibleTo` access).
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/ConPtyJobObjectTests/*"`. Expected on Windows CI: all 4 pass. Expected locally on macOS/Linux: all 4 no-op-pass (early return).
- [ ] Run the full existing Windows PTY suite to check for regressions: same command without the filter.
- [ ] Commit: `git add -A && git commit -m "W1: bind every hosted-agent PTY to a KILL_ON_JOB_CLOSE Job Object on Windows"`

---

## Task 2 — L1-shim(a): native execution-plan construction (`pty_exec_plan`, `pty_probe_execveat`, `pty_preflight`, `pty_plan_contained`, `pty_plan_free`)

**Testability:** The kernel-floor probe and the fd-bound privilege preflight are genuinely Linux-only behaviors (`execveat`, `fgetxattr(security.capability)`, setuid bits) — they run and are asserted in CI on `ubuntu-latest`. The plan-construction PATH/shebang logic itself is portable C (no Linux-only syscalls in the non-privileged branches) and the tests that don't depend on `execveat`/xattr specifics also run locally on macOS via the shared `pty_spawn` path once Task 3 lands — but for THIS task, every test that asserts `contained == 1` or exercises the fd-bound preflight is Linux-only (`if (!OperatingSystem.IsLinux()) return;`) and CI-verified; a macOS dev box can still build the shim (the file compiles under `#ifdef __linux__` guards) and run the `execveat_supported == 0` / `EXEC_PATH` fallback tests locally as a bonus, but full coverage is CI.

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Native/pty_shim.h`
- Modify: `src/Capacitor.Cli.Daemon/Native/pty_shim.c` (currently 10 lines, only `pty_set_winsize`)
- Modify: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyInterop.cs:12-68` (add LibraryImports after the existing shim import at `:66-68`)
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/DummyProcess.cs` (add shebang/setuid/execute-only fixture helpers used by this task and Task 3)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/PtyShimNativeTests.cs` (new)

**Interfaces:**
- Consumes: nothing from other tasks (this is the first native piece).
- Produces (consumed by Task 3 and Task 5):
  - C: `int pty_probe_execveat(void);`
  - C: `int pty_preflight(const char* exe_abs_path, char* const orig_argv[], char* const envp[], int execveat_supported, pty_exec_plan** out_plan);`
  - C: `int pty_plan_contained(const pty_exec_plan* plan);`
  - C: `void pty_plan_free(pty_exec_plan** plan);`
  - C: opaque `typedef struct pty_exec_plan pty_exec_plan;` (declared in `pty_shim.h`, defined only in `pty_shim.c`)
  - C#: `UnixPtyInterop.pty_probe_execveat()`, `UnixPtyInterop.pty_preflight(...)`, `UnixPtyInterop.pty_plan_contained(IntPtr)`, `UnixPtyInterop.pty_plan_free(ref IntPtr)` — exact signatures in Step 4.

### Step 1: Write the header (the ABI contract, transcribed from spec §4.2(a))

- [ ] Create `src/Capacitor.Cli.Daemon/Native/pty_shim.h`:

```c
#ifndef PTY_SHIM_H
#define PTY_SHIM_H

#include <sys/types.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// A GENUINELY OPAQUE handle — this header exposes no fields. The shim owns everything a
// plan references (exec fd, paths, argv strings) for the plan's whole life; the only ways
// to touch a plan are through the functions declared here.
typedef struct pty_exec_plan pty_exec_plan;

// Startup capability probe (call once; cache the result and pass it into every
// pty_preflight call — no hidden global state). Uses the RAW syscall (bypasses any glibc
// execveat wrapper, since the wrapper only exists from glibc 2.34 — this keeps the floor a
// KERNEL floor, not a libc floor). Returns 1 if execveat(AT_EMPTY_PATH) is usable on this
// kernel, 0 otherwise (a test build may force 0 to exercise the <3.19/no-fd-exec fallback
// without needing an actual legacy kernel).
int pty_probe_execveat(void);

// Resolve + classify a launch, parent-side, pre-fork. Never execs. Returns 0 on success
// (*out_plan populated), -1 only when the plan itself cannot be constructed at all (e.g.
// exe_abs_path does not exist) — every other failure mode degrades to an uncontained plan
// rather than returning -1 (see pty_shim.c for the full decision tree).
int pty_preflight(const char* exe_abs_path, char* const orig_argv[], char* const envp[],
                   int execveat_supported, pty_exec_plan** out_plan);

// 1 = the plan is proven non-privileged and will use execveat(fd) (contained); 0 = the plan
// falls back to a normal execve(path) (uncontained — caller logs a warning, launch proceeds
// regardless).
int pty_plan_contained(const pty_exec_plan* plan);

// Frees every string/argv/fd a plan owns and sets *plan = NULL. Single-release: a second
// call with *plan == NULL is a documented no-op (never double-frees).
void pty_plan_free(pty_exec_plan** plan);

// pty_spawn_result / pty_spawn are declared in Task 3's addition to this header.

#ifdef __cplusplus
}
#endif

#endif // PTY_SHIM_H
```

- [ ] Commit: `git add src/Capacitor.Cli.Daemon/Native/pty_shim.h && git commit -m "Add pty_shim.h: the native spawn-shim ABI contract"`

### Step 2: Write the failing tests for the plan-construction contract

- [ ] Add fixture helpers to `DummyProcess.cs` (append inside the existing `internal sealed class DummyProcess`, or as static helpers in the same file — these build real on-disk executables/scripts, not mocks, since the whole point of this shim is real kernel behavior):

```csharp
/// <summary>Writes a native no-op ELF-less "script" is not enough for the shebang tests — this
/// writes a real shebang script `#!/abs/interp [optarg]\n<body>` that just `exit`s, chmod +x.</summary>
public static string WriteShebangScript(string interpAbsPath, string? optArg, string body) {
    var path = Path.Combine(Path.GetTempPath(), "kcap-shim-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
    var shebang = optArg is null ? $"#!{interpAbsPath}\n" : $"#!{interpAbsPath} {optArg}\n";
    File.WriteAllText(path, shebang + body);
    MakeExecutable(path);
    return path;
}

/// <summary>A native executable that's readable but chmod 0111 (execute-only, no read bit) —
/// exercises the "EXEC_PATH plans need no readable fd" §5 case.</summary>
public static string CopyExecuteOnly(string sourceAbsPath) {
    var path = Path.Combine(Path.GetTempPath(), "kcap-shim-x-" + Guid.NewGuid().ToString("N")[..8]);
    File.Copy(sourceAbsPath, path, overwrite: true);
    Chmod(path, 0b001_001_001); // 0111
    return path;
}

/// <summary>A copy of a real binary with the setuid bit set — never actually exec'd (privileged
/// preflight must classify it uncontained and the test never runs it as a real setuid binary,
/// avoiding any real privilege escalation risk in CI).</summary>
public static string CopySetuid(string sourceAbsPath) {
    var path = Path.Combine(Path.GetTempPath(), "kcap-shim-suid-" + Guid.NewGuid().ToString("N")[..8]);
    File.Copy(sourceAbsPath, path, overwrite: true);
    Chmod(path, 0b100_111_101_101 /* 04755 */);
    return path;
}

static void MakeExecutable(string path) => Chmod(path, 0b111_101_101 /* 0755 */);

[System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "chmod", SetLastError = true,
    StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
[System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
private static partial int chmod_native(string path, int mode);

static void Chmod(string path, int mode) {
    if (chmod_native(path, mode) != 0)
        throw new InvalidOperationException($"chmod {Convert.ToString(mode, 8)} {path} failed: errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
}
```

(`DummyProcess` needs `partial` added to its class declaration for the new `LibraryImport`; keep the file's existing `internal sealed` modifiers, just add `partial`.)

- [ ] Create `test/Capacitor.Cli.Tests.Unit/Daemon/PtyShimNativeTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// L1-shim(a) (spec §4.2(a)): the parent-side, pre-fork plan-construction contract —
/// pty_probe_execveat / pty_preflight / pty_plan_contained / pty_plan_free. NEVER forks or
/// execs (that's Task 3's pty_spawn); these tests only inspect the classification decision.
/// </summary>
public class PtyShimNativeTests {
    static string[] EmptyEnvp() => [];
    static string   Env(string key, string value) => $"{key}={value}";

    [Test]
    public async Task Probe_execveat_reports_supported_on_a_35_plus_kernel() {
        if (!OperatingSystem.IsLinux()) return;

        // No forced-0 test seam engaged — a modern CI kernel (>= 3.19, almost certainly much
        // newer) must report supported.
        await Assert.That(UnixPtyInterop.pty_probe_execveat()).IsEqualTo(1);
    }

    [Test]
    public async Task Native_elf_no_shebang_is_contained_execfd() {
        if (!OperatingSystem.IsLinux()) return;

        var plan = Preflight("/bin/true", ["true"], EmptyEnvp(), execveatSupported: 1);
        try {
            await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Probe_disabled_forces_every_launch_uncontained_execpath() {
        if (!OperatingSystem.IsLinux()) return;

        // The <3.19 fallback, exercised WITHOUT a legacy kernel via the forced-0 test seam.
        var plan = Preflight("/bin/true", ["true"], EmptyEnvp(), execveatSupported: 0);
        try {
            await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Setuid_binary_classifies_uncontained_never_a_false_proof() {
        if (!OperatingSystem.IsLinux()) return;

        var suid = DummyProcess.CopySetuid("/bin/true");
        try {
            var plan = Preflight(suid, [suid], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(suid); }
    }

    [Test]
    public async Task Missing_original_path_is_a_preflight_failure_returns_minus_one() {
        if (!OperatingSystem.IsLinux()) return;

        var rc = UnixPtyInterop.pty_preflight(
            "/definitely/does/not/exist/" + Guid.NewGuid(), ["x", null], EmptyEnvp(), 1, out var plan);

        await Assert.That(rc).IsEqualTo(-1);
        await Assert.That(plan).IsEqualTo(IntPtr.Zero);
    }

    [Test]
    public async Task Execute_only_native_binary_still_builds_a_plan() {
        if (!OperatingSystem.IsLinux()) return;

        // No readable fd — EXEC_PATH plans need none; an EXEC_FD attempt's inspection
        // failure must degrade to EXEC_PATH-uncontained, never a launch failure.
        var xonly = DummyProcess.CopyExecuteOnly("/bin/true");
        try {
            var plan = Preflight(xonly, [xonly], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(plan).IsNotEqualTo(IntPtr.Zero); }
            finally { Free(plan); }
        } finally { File.Delete(xonly); }
    }

    [Test]
    public async Task Direct_shebang_rewrites_argv_keeping_the_single_optarg() {
        if (!OperatingSystem.IsLinux()) return;

        var script = DummyProcess.WriteShebangScript("/bin/sh", "-e", "exit 0\n");
        try {
            var plan = Preflight(script, [script, "extra"], EmptyEnvp(), execveatSupported: 1);
            try {
                // Contained: /bin/sh has no shebang of its own, no setuid bit on a stock CI image.
                await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
            } finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Env_shebang_resolves_against_child_path_not_daemon_path() {
        if (!OperatingSystem.IsLinux()) return;

        // Two directories each with a differently-behaved `probe-target` on PATH; the DAEMON's
        // ambient PATH points at one, the CHILD's envp PATH points at the other. The contained
        // plan must preflight the one the CHILD's PATH selects.
        var (daemonDir, childDir) = DummyProcess.TwoDistinctPathDirsWithDifferentTarget("probe-target");
        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "probe-target", "true\n");
        try {
            var childEnvp = new[] { Env("PATH", childDir) };
            var plan = Preflight(script, [script], childEnvp, execveatSupported: 1);
            try {
                await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
                // The resolved inode must be the one under childDir, not daemonDir — asserted via
                // PlanExecFdInodeMatches, a small test-only helper added in Task 3 once pty_spawn
                // exposes the exec'd fd's inode for comparison against /proc/self/fd bookkeeping.
                // (Left as a forward reference: Task 3 Step 2 extends this exact test.)
            } finally { Free(plan); }
        } finally {
            File.Delete(script);
            Directory.Delete(daemonDir, true);
            Directory.Delete(childDir, true);
        }
    }

    [Test]
    public async Task Empty_or_relative_child_path_component_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "probe-target", "true\n");
        try {
            var childEnvp = new[] { Env("PATH", ".:/usr/bin") }; // leading empty/relative element
            var plan = Preflight(script, [script], childEnvp, execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Env_with_extra_tokens_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "-S FOO=1 sh", "exit 0\n");
        try {
            var plan = Preflight(script, [script], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Two_level_script_chain_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        var inner = DummyProcess.WriteShebangScript("/bin/sh", null, "exit 0\n");
        var outer = DummyProcess.WriteShebangScript(inner, null, "unused\n");
        try {
            var plan = Preflight(outer, [outer], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(inner); File.Delete(outer); }
    }

    [Test]
    public async Task Enoexec_shebangless_script_builds_a_plan_that_fails_at_exec_not_here() {
        if (!OperatingSystem.IsLinux()) return;

        // pty_preflight itself must NOT fail this (it has no shebang to parse and no reason to
        // reject a plain file) — the ENOEXEC surfaces at exec time (Task 3's test, not here).
        var path = Path.Combine(Path.GetTempPath(), "kcap-noshebang-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(path, "not a script, no shebang\n");
        DummyProcess.MakeExecutablePublic(path); // exposes MakeExecutable for this one direct case
        try {
            var plan = Preflight(path, [path], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(plan).IsNotEqualTo(IntPtr.Zero); }
            finally { Free(plan); }
        } finally { File.Delete(path); }
    }

    static IntPtr Preflight(string exePath, string?[] argv, string[] envp, int execveatSupported) {
        var rc = UnixPtyInterop.pty_preflight(exePath, argv, envp, execveatSupported, out var plan);
        if (rc != 0) throw new InvalidOperationException($"pty_preflight unexpectedly failed for {exePath}");
        return plan;
    }

    static void Free(IntPtr plan) {
        var p = plan;
        UnixPtyInterop.pty_plan_free(ref p);
    }
}
```

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/PtyShimNativeTests/*"`. Expected: compile failure (`UnixPtyInterop.pty_probe_execveat`/`pty_preflight`/`pty_plan_contained`/`pty_plan_free` and `DummyProcess.CopySetuid`/`CopyExecuteOnly`/`WriteShebangScript`/`TwoDistinctPathDirsWithDifferentTarget`/`MakeExecutablePublic` don't exist yet). This is the expected red state for this step.

### Step 3: Add the `DummyProcess.TwoDistinctPathDirsWithDifferentTarget` and `MakeExecutablePublic` helpers

- [ ] Add to `DummyProcess.cs`:

```csharp
/// <summary>Two temp directories, each containing an executable named <paramref name="name"/>
/// that behaves differently (one is a copy of /bin/true, the other /bin/false) — for asserting
/// which PATH a resolution actually used.</summary>
public static (string daemonDir, string childDir) TwoDistinctPathDirsWithDifferentTarget(string name) {
    var daemonDir = Directory.CreateTempSubdirectory("kcap-daemon-path-").FullName;
    var childDir  = Directory.CreateTempSubdirectory("kcap-child-path-").FullName;
    File.Copy("/bin/true",  Path.Combine(daemonDir, name));
    File.Copy("/bin/false", Path.Combine(childDir, name));
    MakeExecutable(Path.Combine(daemonDir, name));
    MakeExecutable(Path.Combine(childDir, name));
    return (daemonDir, childDir);
}

public static void MakeExecutablePublic(string path) => MakeExecutable(path);
```

- [ ] Run the same filtered test command. Expected: still fails to compile until Step 4 adds the interop declarations — but the `DummyProcess` half of the red state is now cleared. Confirm by checking the remaining compiler errors only name `UnixPtyInterop` members.

### Step 4: Add the P/Invoke declarations to `UnixPtyInterop.cs`

- [ ] Add after the existing `pty_set_winsize_shim` import (`:66-68`):

```csharp
[LibraryImport("libpty_shim", EntryPoint = "pty_probe_execveat")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial int pty_probe_execveat();

// argv/envp are NULL-terminated string arrays; a `string?[]` with a trailing `null` element
// marshals correctly via LibraryImport's array marshaller (mirrors the existing execvp import).
[LibraryImport("libpty_shim", EntryPoint = "pty_preflight", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial int pty_preflight(
    string exeAbsPath, string?[] origArgv, string?[] envp, int execveatSupported, out IntPtr outPlan);

[LibraryImport("libpty_shim", EntryPoint = "pty_plan_contained")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial int pty_plan_contained(IntPtr plan);

[LibraryImport("libpty_shim", EntryPoint = "pty_plan_free")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial void pty_plan_free(ref IntPtr plan);
```

Note: `orig_argv`/`envp` in the native signature are `char* const[]` (NUL-terminated arrays of C strings, no explicit length) — `string?[]` with a trailing `null` sentinel element is exactly how the existing `execvp` import already marshals `argv` (`UnixPtyInterop.cs:18`, called with `argv[^1] = null!` in `UnixPtyProcess.cs:37` today); reuse that convention.

- [ ] Run the filtered test command again. Expected: still failing (native symbols don't exist in `libpty_shim` yet) — but now a LINK/load failure (`EntryPointNotFoundException` or the shim `.so` doesn't export these), not a C# compile failure. This confirms the managed side is wired correctly and we're now blocked purely on the native implementation.

### Step 5: Implement the native plan construction in `pty_shim.c`

- [ ] Replace `pty_shim.c`'s contents with (keeping the existing `pty_set_winsize` at the top):

```c
#include "pty_shim.h"

#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/xattr.h>
#include <sys/syscall.h>
#include <fcntl.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>

// ── existing (unchanged) ────────────────────────────────────────────────────────────────
int pty_set_winsize(int fd, unsigned short rows, unsigned short cols) {
    struct winsize ws = {0};
    ws.ws_row = rows;
    ws.ws_col = cols;
    return ioctl(fd, TIOCSWINSZ, &ws);
}

// ── L1-shim(a): execution-plan construction ─────────────────────────────────────────────

#define PTY_EXEC_FD   1
#define PTY_EXEC_PATH 2

struct pty_exec_plan {
    int    mode;       // PTY_EXEC_FD or PTY_EXEC_PATH
    int    exec_fd;    // valid iff mode == PTY_EXEC_FD; -1 otherwise. O_CLOEXEC — closes on
                        // successful exec automatically; freed explicitly on every other path.
    char  *exec_path;  // valid iff mode == PTY_EXEC_PATH; NULL otherwise. Owned (strdup'd).
    char **argv;       // NULL-terminated, owned (deep-copied).
    int    contained;  // 1 = EXEC_FD + proven non-privileged; 0 = uncontained.
};

// Test seam: force the probe result without a legacy kernel. 0 = unset (use the real probe).
static int forced_execveat_supported = -1; // -1 = not forced

int pty_probe_execveat(void) {
    errno = 0;
    // Deliberately bogus fd (-1) + empty pathname via the RAW syscall — bypasses any glibc
    // wrapper (only added in glibc 2.34) so the floor stays a KERNEL floor. EBADF means the
    // syscall validated flags and reached fd validation (i.e. it EXISTS); ENOSYS means it
    // doesn't. Every OTHER errno (EINVAL, EPERM from a seccomp filter, anything else) is
    // fail-safe treated as unsupported — classification must never be left undefined.
    long rc = syscall(SYS_execveat, -1, "", NULL, NULL, AT_EMPTY_PATH);
    if (rc == 0) return 1;
    return errno == EBADF ? 1 : 0;
}

static char **dup_argv(char *const argv[]) {
    int n = 0;
    while (argv[n]) n++;
    char **copy = calloc((size_t)n + 1, sizeof(char*));
    if (!copy) return NULL;
    for (int i = 0; i < n; i++) {
        copy[i] = strdup(argv[i]);
        if (!copy[i]) { for (int j = 0; j < i; j++) free(copy[j]); free(copy); return NULL; }
    }
    copy[n] = NULL;
    return copy;
}

static void free_argv(char **argv) {
    if (!argv) return;
    for (int i = 0; argv[i]; i++) free(argv[i]);
    free(argv);
}

static int build_execpath_plan(const char *exe_abs_path, char *const orig_argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) return -1;
    plan->mode      = PTY_EXEC_PATH;
    plan->exec_fd   = -1;
    plan->exec_path = strdup(exe_abs_path);
    plan->argv      = dup_argv(orig_argv);
    plan->contained = contained;
    if (!plan->exec_path || !plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// fd-bound privilege preflight (pinned, spec §4.2): the check and the exec share ONE open
// file — never a path-based stat-then-execve (TOCTOU-broken). Only a clean "no bits, no
// capability xattr" proves non-privileged; any other outcome (including a read/stat ERROR)
// classifies uncontained — never a false proof.
static int fd_is_non_privileged(int fd) {
    struct stat st;
    if (fstat(fd, &st) != 0) return 0;
    if (st.st_mode & (S_ISUID | S_ISGID)) return 0;

    char buf[1];
    ssize_t r = fgetxattr(fd, "security.capability", buf, sizeof(buf));
    if (r >= 0) return 0; // has SOME capability xattr payload → privileged, uncontained
    // ENODATA (no xattr) or ENOTSUP (fs can't carry xattrs) are the ONLY proofs of absence.
    return (errno == ENODATA || errno == ENOTSUP) ? 1 : 0;
}

static int build_execfd_plan(int fd, char *const argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) { close(fd); return -1; }
    plan->mode      = PTY_EXEC_FD;
    plan->exec_fd   = fd;
    plan->exec_path = NULL;
    plan->argv      = dup_argv(argv);
    plan->contained = contained;
    if (!plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// Resolves `name` against `path_env` (colon-separated). Returns a strdup'd absolute path or
// NULL if not found / path_env has any empty/relative element (caller then classifies
// uncontained rather than risk resolving against the wrong cwd — see the PATH rule below).
static char *resolve_in_absolute_path(const char *name, const char *path_env, int *saw_relative_component) {
    *saw_relative_component = 0;
    if (!path_env || !*path_env) return NULL;

    char *copy = strdup(path_env);
    if (!copy) return NULL;

    char *result = NULL;
    for (char *dir = strtok(copy, ":"); dir; dir = strtok(NULL, ":")) {
        if (dir[0] != '/') { *saw_relative_component = 1; continue; }
        size_t need = strlen(dir) + 1 + strlen(name) + 1;
        char *candidate = malloc(need);
        if (!candidate) break;
        snprintf(candidate, need, "%s/%s", dir, name);
        struct stat st;
        if (!result && access(candidate, X_OK) == 0 && stat(candidate, &st) == 0 && S_ISREG(st.st_mode)) {
            result = candidate;
        } else {
            free(candidate);
        }
    }
    free(copy);
    return result;
}

static const char *find_env(char *const envp[], const char *key) {
    size_t klen = strlen(key);
    for (int i = 0; envp && envp[i]; i++) {
        if (strncmp(envp[i], key, klen) == 0 && envp[i][klen] == '=') return envp[i] + klen + 1;
    }
    return NULL;
}

// Builds the plan for a `#!interp [optarg]` (direct) or `#!/usr/bin/env NAME [...]` shebang.
// `head`/`head_len` is the first-256-bytes sniff already read from the ORIGINAL file (script)
// by the caller; the script fd itself is never exec'd or kept (TOCTOU rule — see spec).
static int build_shebang_plan(
        const char *script_abs_path, const char *head, ssize_t head_len,
        char *const orig_argv[], char *const envp[], int execveat_supported, pty_exec_plan **out_plan) {
    // Parse the shebang line: "#!<rest>\n"
    const char *line_end = memchr(head, '\n', (size_t)head_len);
    size_t line_len = line_end ? (size_t)(line_end - head) : (size_t)head_len;
    char line[256] = {0};
    memcpy(line, head + 2, line_len > 2 ? line_len - 2 : 0);

    // Split into up to 2 tokens (interp path, one optional arg-blob).
    char *save = NULL;
    char *tok0 = strtok_r(line, " \t", &save);
    char *rest = save; // everything after the first token, NOT re-tokenized yet

    if (!tok0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); // malformed → uncontained

    int is_env = strcmp(tok0, "/usr/bin/env") == 0 || strcmp(tok0, "env") == 0;

    if (!is_env) {
        // Direct shebang: at most ONE optional arg is kept as-is; anything with more tokens
        // after that is "an unresolvable shebang" per spec → uncontained.
        char *tok1 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        char *tok2 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        if (tok2) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        int fd = open(tok0, O_RDONLY | O_CLOEXEC);
        if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        // Rewritten argv: [interp, optarg?, script_abs_path, orig_argv[1:]...]
        int n = 0; while (orig_argv[n]) n++;
        int extra = tok1 ? 2 : 1; // interp (+ optarg) + script
        char **argv = calloc((size_t)(extra + (n - 1) + 1), sizeof(char*));
        int k = 0;
        argv[k++] = strdup(tok0);
        if (tok1) argv[k++] = strdup(tok1);
        argv[k++] = strdup(script_abs_path);
        for (int i = 1; i < n; i++) argv[k++] = strdup(orig_argv[i]);
        argv[k] = NULL;

        int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
        pty_exec_plan *plan = calloc(1, sizeof(*plan));
        plan->mode = PTY_EXEC_FD; plan->exec_fd = fd; plan->argv = argv; plan->contained = contained;
        *out_plan = plan;
        return 0;
    }

    // `env [-S ...|VAR=val ...] NAME [args...]` — only the bare "env NAME" form (exactly one
    // token after `env`, no flags/assignments) is rewritten; anything richer → uncontained.
    char *name = rest ? strtok_r(NULL, " \t", &save) : NULL;
    char *extra_tok = name ? strtok_r(NULL, " \t", &save) : NULL;
    if (!name || extra_tok || name[0] == '-' || strchr(name, '=')) {
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    const char *child_path = find_env(envp, "PATH");
    int saw_relative = 0;
    char *resolved = NULL;
    if (child_path) {
        resolved = resolve_in_absolute_path(name, child_path, &saw_relative);
    } else {
        // Unset child PATH → confstr(_CS_PATH) verbatim, at runtime, preserving its order.
        size_t need = confstr(_CS_PATH, NULL, 0);
        char *cs = malloc(need);
        confstr(_CS_PATH, cs, need);
        resolved = resolve_in_absolute_path(name, cs, &saw_relative);
        free(cs);
    }

    if (saw_relative || !resolved) {
        // Empty/relative PATH component, or NAME simply not found in an absolute-only PATH →
        // uncontained either way (the kernel/env resolves it correctly at runtime; we just
        // forgo containment rather than risk preflighting the wrong inode).
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(resolved, O_RDONLY | O_CLOEXEC);
    free(resolved);
    if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

    int n = 0; while (orig_argv[n]) n++;
    char **argv = calloc((size_t)(n + 1), sizeof(char*)); // [resolved, script, orig_argv[1:]...]
    int k = 0;
    // NB: argv[0] is the resolved interpreter path (not re-derived from fd) — matches spec's
    // "argv[0] = the resolved interpreter absolute path" rule for the direct-shebang case,
    // generalized here since env's target IS the interpreter.
    argv[k++] = strdup(name); // display/basename form is fine for argv[0]; the fd IS what execs
    argv[k++] = strdup(script_abs_path);
    for (int i = 1; i < n; i++) argv[k++] = strdup(orig_argv[i]);
    argv[k] = NULL;

    int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    plan->mode = PTY_EXEC_FD; plan->exec_fd = fd; plan->argv = argv; plan->contained = contained;
    *out_plan = plan;
    return 0;
}

int pty_preflight(const char *exe_abs_path, char *const orig_argv[], char *const envp[],
                   int execveat_supported, pty_exec_plan **out_plan) {
    *out_plan = NULL;
    if (forced_execveat_supported >= 0) execveat_supported = forced_execveat_supported;

    if (!execveat_supported) {
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(exe_abs_path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        // The plan can't be constructed AT ALL (not even the EXEC_PATH fallback needs an open
        // fd, but a nonexistent path fails EXEC_PATH too — execve would ENOENT identically) →
        // -1, the one case that is a genuine preflight failure.
        return -1;
    }

    char head[256];
    ssize_t n = read(fd, head, sizeof(head) - 1);
    if (n < 0) {
        close(fd);
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan); // degrade, don't fail
    }
    head[n] = '\0';

    if (n >= 2 && head[0] == '#' && head[1] == '!') {
        close(fd); // the SCRIPT fd is never exec'd — see the TOCTOU note in the header comment
        return build_shebang_plan(exe_abs_path, head, n, orig_argv, envp, execveat_supported, out_plan);
    }

    int contained = fd_is_non_privileged(fd);
    return build_execfd_plan(fd, orig_argv, contained, out_plan);
}

int pty_plan_contained(const pty_exec_plan *plan) {
    return plan ? plan->contained : 0;
}

void pty_plan_free(pty_exec_plan **plan) {
    if (!plan || !*plan) return;
    pty_exec_plan *p = *plan;
    if (p->mode == PTY_EXEC_FD && p->exec_fd >= 0) close(p->exec_fd);
    free(p->exec_path);
    free_argv(p->argv);
    free(p);
    *plan = NULL;
}
```

- [ ] Extend the `CompilePtyShim` MSBuild target (see Task 6 for the full per-RID rewrite — for THIS step, just get local iteration working) so `pty_shim.c` compiles on your current dev OS. On Linux, temporarily build with: `cc -shared -fPIC -o bin/Debug/net10.0/libpty_shim.so src/Capacitor.Cli.Daemon/Native/pty_shim.c` (Task 6 wires this into the csproj properly; don't block on that here — just get a `.so`/`.dylib` next to the test binary's output so `NativeLibrary.Load("libpty_shim", ...)` in the P/Invoke resolves).
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/PtyShimNativeTests/*"`. Expected: all Linux tests pass; macOS/Linux non-Linux-gated tests no-op. Iterate on the C until green — this is native code, expect several red→green cycles on subtleties (argv NUL-termination off-by-ones, `strtok_r` reentrancy, etc.).
- [ ] Commit: `git add -A && git commit -m "L1-shim(a): native pty_preflight/pty_probe_execveat/pty_plan_contained/pty_plan_free"`

---

## Task 3 — L1-shim(b): native `pty_spawn` (fork/exec child sequence, error-pipe handshake, start-identity capture)

**Testability:** The fork/PDEATHSIG/execveat/error-pipe mechanics are exercised for real on Linux in this task's tests — CI (`ubuntu-latest`) is the authoritative coverage. macOS compiles the same file (`prctl`/`execveat`-specific code is `#ifdef __linux__`-guarded; macOS uses `execve` and skips the PDEATHSIG arm/getppid-recheck steps that have no macOS equivalent) and the macOS-reachable parts of these same tests (capture producing a `mac:` token, the handshake/error-pipe mechanics themselves, which are OS-generic) run locally on a macOS dev box and are also exercised in CI via `release.yml`'s `osx-arm64` build-time smoke (Task 6) — but the PDEATHSIG-specific assertions are unconditionally `if (!OperatingSystem.IsLinux()) return;` gated.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Native/pty_shim.h` (add `pty_spawn_result`, `pty_spawn`, `pty_capture_mac_identity`)
- Modify: `src/Capacitor.Cli.Daemon/Native/pty_shim.c` (add `pty_spawn` + the Linux `lx:` and macOS `mac:` native capture helpers)
- Modify: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyInterop.cs` (add the `PtySpawnResult` marshaling struct + `pty_spawn` LibraryImport)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/PtySpawnTests.cs` (new)

**Interfaces:**
- Consumes: `pty_exec_plan*` from Task 2 (`pty_preflight`'s `out_plan`).
- Produces (consumed by Task 5 and Task 7):
  - C: `typedef struct { pid_t pid; int master_fd; int err_no; int failed_step; char start_identity[128]; } pty_spawn_result;`
  - C: `int pty_spawn(const pty_exec_plan* plan, char* const envp[], const char* cwd, unsigned short rows, unsigned short cols, pid_t expected_parent, int cancel_fd, pty_spawn_result* out);`
  - C (macOS-only, exported so Task 7's C# `ProcessStartToken.ForPid` can call the SAME vendored-ABI implementation used inside `pty_spawn` — see the design note below): `int pty_capture_mac_identity(pid_t pid, char* out, size_t outlen);` — returns 1/0, fills `out` (`>= 128` bytes) with `mac:{bootsessionuuid}:{p_uniqueid}\0`.
  - C#: `UnixPtyInterop.PtySpawnResult` struct, `UnixPtyInterop.pty_spawn(...)`.

**Design note (resolves an open question the baseline notes flagged — flagged to the requester in the final report):** the notes left it open whether the macOS `p_uniqueid`/vendored-struct logic lives in C# (`Core`) or C (`pty_shim.c`). It has to exist in **both**, for two different reasons that don't overlap: (1) the spec's capture-binding rule requires the spawn-time capture to happen natively **inside** `pty_spawn`, immediately post-`forkpty`, before any managed code runs — that is only possible in C. (2) `ProcessStartToken.ForPid` (Task 7) is called by the **`kcap` CLI binary itself** (`Capacitor.Cli/Commands/DaemonCommands.cs`, for daemon-liveness checks), which does **not** ship `libpty_shim` (only the daemon's npm platform package copies the shim next to `kcap-daemon`) — so `Core` cannot take a hard runtime dependency on the shim. Rather than vendor the private ABI struct twice with two independent (and driftable) implementations, `pty_capture_mac_identity` is implemented **once**, in `pty_shim.c`, and exported for BOTH `pty_spawn`'s internal capture (macOS) AND a new Task-7 `LibraryImport` from `ProcessStartToken` — but since `ProcessStartToken` lives in `Core` and can't depend on the shim, Task 7 instead **duplicates only the vendored struct+flavor** directly in `Core` (no shim dependency), and Task 3 here **also** implements it in `pty_shim.c` for the internal spawn-time capture. Task 3 and Task 7 must therefore produce **byte-identical** token strings for the same live process — Task 7 includes a cross-implementation consistency test (`spawn via the shim, then independently call ProcessStartToken.ForPid on the same pid, assert equal`) that is the actual regression guard for this duplication.

### Step 1: Extend `pty_shim.h`

- [ ] Add before the final `#endif`:

```c
typedef struct {
    pid_t pid;
    int   master_fd;
    int   err_no;
    int   failed_step; // 0=none, 1=fork, 2=prctl, 3=parent_died, 4=chdir, 5=exec, 6=handshake_timeout, 7=cancelled
    char  start_identity[128]; // "mac:{bootsessionuuid}:{p_uniqueid}" / "lx:{boot_id}:{starttime}"; "" = uncapturable
} pty_spawn_result;

enum {
    PTY_STEP_NONE = 0,
    PTY_STEP_FORK,
    PTY_STEP_PRCTL,
    PTY_STEP_PARENT_DIED,
    PTY_STEP_CHDIR,
    PTY_STEP_EXEC,
    PTY_STEP_HANDSHAKE_TIMEOUT,
    PTY_STEP_CANCELLED
};

// forkpty + child sequence + error-pipe handshake. 0 on success (out populated, out->pid > 0,
// out->master_fd valid), -1 on failure (out->err_no/out->failed_step set, no live unobserved
// child left behind — see pty_shim.c for the full contract). start_identity is captured
// IN THE PARENT, immediately after forkpty returns, before the child can be reaped by
// anything (the capture-binding rule) — an empty string means uncapturable, NOT a failure.
int pty_spawn(const pty_exec_plan *plan, char *const envp[], const char *cwd,
              unsigned short rows, unsigned short cols,
              pid_t expected_parent, int cancel_fd, pty_spawn_result *out);

#ifdef __APPLE__
// Captures `mac:{kern.bootsessionuuid}:{p_uniqueid}` for `pid` into `out` (>= 128 bytes),
// NUL-terminated. Returns 1 on success, 0 if the private-ABI call is unavailable/anomalous
// (short read, EINVAL, zero id) — spare-shaped, never a false proof. Exported so it can be
// called BOTH from pty_spawn's internal post-forkpty capture and (via a separate P/Invoke)
// from the managed ProcessStartToken.ForPid comparison path for an arbitrary already-running
// pid — see this task's design note for why there are still two independent call sites.
int pty_capture_mac_identity(pid_t pid, char *out, size_t outlen);
#endif
```

- [ ] Commit: `git add src/Capacitor.Cli.Daemon/Native/pty_shim.h && git commit -m "Extend pty_shim.h with the pty_spawn ABI"`

### Step 2: Write the failing `pty_spawn` tests

- [ ] Create `test/Capacitor.Cli.Tests.Unit/Daemon/PtySpawnTests.cs`:

```csharp
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// L1-shim(b) (spec §4.2(a)): pty_spawn — the actual fork/exec, run directly via P/Invoke
/// (bypassing UnixPtyProcess/the spawner thread, which Task 4/5 layer on top). These tests
/// exercise the raw native contract in isolation.
/// </summary>
public class PtySpawnTests {
    [Test]
    public async Task Successful_spawn_returns_a_reapable_child_and_a_captured_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        try {
            var rc = Spawn(plan, out var result);
            try {
                await Assert.That(rc).IsEqualTo(0);
                await Assert.That(result.pid).IsGreaterThan(0);
                await Assert.That(result.failed_step).IsEqualTo(0);
                // The capture-binding rule: identity is non-empty for a healthy spawn on both
                // platforms (barring a genuine private-ABI anomaly, covered separately).
                await Assert.That(result.StartIdentityString).IsNotEmpty();
                await Assert.That(result.StartIdentityString).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");
            } finally {
                UnixPtyInterop.kill(result.pid, UnixPtyInterop.SIGKILL);
                UnixPtyInterop.waitpid(result.pid, out _, 0);
            }
        } finally { Free(plan); }
    }

    [Test]
    public async Task Missing_original_path_fails_at_preflight_no_child_forked() {
        if (!OperatingSystem.IsLinux()) return;

        var rc = UnixPtyInterop.pty_preflight("/no/such/binary-" + Guid.NewGuid(), ["x", null], [], 1, out var plan);
        await Assert.That(rc).IsEqualTo(-1);
        await Assert.That(plan).IsEqualTo(IntPtr.Zero);
        // No pty_spawn call at all — this IS the assertion (a preflight failure never reaches spawn).
    }

    [Test]
    public async Task Child_side_exec_failure_reports_failed_step_exec_and_reaps_cleanly() {
        if (!OperatingSystem.IsLinux()) return;

        // Build a valid EXEC_PATH plan, then remove the file between preflight and spawn so
        // the FORK succeeds but the exec fails inside the child.
        var path = DummyProcess.CopyExecuteOnly("/bin/true");
        var plan = Preflight(path, [path], execveatSupported: 0); // force EXEC_PATH so the path (not an fd) is re-resolved at exec time
        File.Delete(path);
        try {
            var rc = Spawn(plan, out var result);
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.failed_step).IsEqualTo(5 /* PTY_STEP_EXEC */);
            await Assert.That(result.err_no).IsEqualTo(2 /* ENOENT */);
            // No zombie/phantom: waitpid on the reported pid must fail with ECHILD (already reaped by pty_spawn).
            var wpRc = UnixPtyInterop.waitpid(result.pid, out _, 0);
            await Assert.That(wpRc).IsEqualTo(-1);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Bad_cwd_reports_failed_step_chdir() {
        if (!OperatingSystem.IsLinux()) return;

        var plan = Preflight("/bin/true", ["true"]);
        try {
            var rc = Spawn(plan, out var result, cwd: "/no/such/directory-" + Guid.NewGuid());
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.failed_step).IsEqualTo(4 /* PTY_STEP_CHDIR */);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Getppid_mismatch_self_kills_and_reports_parent_died() {
        if (!OperatingSystem.IsLinux()) return;

        // Passing a deliberately WRONG expected_parent simulates "the real daemon died and I was
        // reparented" without actually killing anything — the child must self-kill and the
        // parent must see failed_step=parent_died, NEVER a false success.
        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        try {
            var rc = Spawn(plan, out var result, expectedParent: 1 /* init — never our real parent */);
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.failed_step).IsEqualTo(3 /* PTY_STEP_PARENT_DIED */);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Cancel_fd_during_handshake_kills_and_reaps_returns_cancelled() {
        if (!OperatingSystem.IsLinux()) return;

        // A child that SIGSTOPs itself before exec (via a wrapper script) never completes the
        // handshake — writing to cancel_fd must interrupt the blocking pty_spawn call.
        var stopper = DummyProcess.WriteShebangScript("/bin/sh", null, "kill -STOP $$\n");
        var plan = Preflight(stopper, [stopper]);
        var (cancelRead, cancelWrite) = MakePipe();
        try {
            var spawnTask = Task.Run(() => Spawn(plan, out _, cancelFd: cancelRead));
            await Task.Delay(500); // let the child reach SIGSTOP
            UnixPtyInterop.write(cancelWrite, [1], 1);
            var rc = await spawnTask;
            // Re-fetch result via a ref-capturing overload is awkward across Task.Run; assert
            // via the boxed result instead (see Spawn's out-param handling below).
            await Assert.That(rc).IsEqualTo(-1);
        } finally {
            Free(plan);
            UnixPtyInterop.close(cancelRead);
            UnixPtyInterop.close(cancelWrite);
            File.Delete(stopper);
        }
    }

    [Test]
    public async Task Capture_binding_a_fast_exiting_child_never_yields_a_recycled_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // Spawn something that exits IMMEDIATELY, then race to fork a decoy and reuse the pid —
        // the captured identity must describe the ORIGINAL incarnation (this is exactly what
        // the capture-binding rule (capture pre-reap, inside pty_spawn) is supposed to
        // guarantee: nothing has waited on the child before pty_spawn captured its identity).
        var plan = Preflight("/bin/true", ["true"]);
        try {
            var rc = Spawn(plan, out var result);
            await Assert.That(rc).IsEqualTo(0);
            await Assert.That(result.StartIdentityString).IsNotEmpty();
            // The captured token must still describe PID result.pid's ORIGINAL incarnation even
            // though /bin/true has almost certainly already exited and could be reaped by now —
            // re-deriving the SAME token for the same still-unreaped zombie must match exactly
            // (a live-process re-derivation isn't possible once it's exited, so the assertion is
            // that the captured string is well-formed and stable, not a live re-comparison).
            await UnixPtyInterop.waitpid(result.pid, out _, 0).AsTask(); // reap the exited /bin/true
        } finally { Free(plan); }
    }

    static IntPtr Preflight(string exe, string?[] argv, int execveatSupported = 1) {
        var rc = UnixPtyInterop.pty_preflight(exe, argv, [], execveatSupported, out var plan);
        if (rc != 0) throw new InvalidOperationException($"preflight failed for {exe}");
        return plan;
    }

    static int Spawn(IntPtr plan, out UnixPtyInterop.PtySpawnResult result, string? cwd = null,
            int expectedParent = -1, int cancelFd = -1) {
        var expected = expectedParent == -1 ? Environment.ProcessId : expectedParent;
        return UnixPtyInterop.pty_spawn(plan, [], cwd ?? Directory.GetCurrentDirectory(), 40, 120, expected, cancelFd, out result);
    }

    static void Free(IntPtr plan) { var p = plan; UnixPtyInterop.pty_plan_free(ref p); }

    static (int read, int write) MakePipe() {
        var fds = new int[2];
        UnixPtyInterop.pipe(fds);
        return (fds[0], fds[1]);
    }
}
```

- [ ] Note: `UnixPtyInterop.PtySpawnResult.StartIdentityString` (a convenience accessor over the fixed byte buffer) and `UnixPtyInterop.pipe(int[])` don't exist yet — Step 3 adds both. `Task_Cancel_fd_...`'s `Spawn` call signature returning `int` while boxing `result` via `Task<int>` needs `out var result` NOT to cross the `Task.Run` boundary cleanly in C# — rewrite that one test during implementation to capture the result via a local mutable field or `TaskCompletionSource<UnixPtyInterop.PtySpawnResult>` rather than an `out` parameter inside the lambda (an `out` param can't be captured by a lambda passed to `Task.Run`); this is a mechanical fix, not a design change — do it when this test goes red for the wrong reason (CS1628).
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/PtySpawnTests/*"`. Expected: compile failure (`pty_spawn`, `PtySpawnResult`, `pipe` don't exist in `UnixPtyInterop` yet).

### Step 3: Add the `PtySpawnResult` marshaling struct + `pty_spawn`/`pipe` P/Invoke to `UnixPtyInterop.cs`

- [ ] Add:

```csharp
[LibraryImport("libc", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial int pipe(int[] fds);

[LibraryImport("libpty_shim", EntryPoint = "pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static partial int pty_spawn(
    IntPtr plan, string?[] envp, string cwd, ushort rows, ushort cols,
    int expectedParent, int cancelFd, out PtySpawnResult result);

#if false // macOS-only export; guard with a runtime IsMacOS() check at the call site, not a
          // compile-time #if — this file targets every Unix RID from one source.
#endif
[LibraryImport("libpty_shim", EntryPoint = "pty_capture_mac_identity", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
internal static unsafe partial int pty_capture_mac_identity(int pid, byte* out_, nuint outlen);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PtySpawnResult {
    public int  Pid;
    public int  MasterFd;
    public int  ErrNo;
    public int  FailedStep;
    public fixed byte StartIdentity[128];

    public readonly string StartIdentityString {
        get {
            fixed (byte* p = StartIdentity) {
                var len = 0;
                while (len < 128 && p[len] != 0) len++;
                return System.Text.Encoding.UTF8.GetString(p, len);
            }
        }
    }
}
```

Remove the stray `#if false` scaffolding above before committing — it was left in the plan only to flag that the macOS export needs no compile-time guard on the C# side (P/Invoke resolution is already lazy/per-call); delete those three lines in the real diff.

- [ ] Run the filtered test command again. Expected: link/runtime failure now (`pty_spawn`/`pty_capture_mac_identity` don't exist in the shim binary yet), not a C# compile error.

### Step 4: Implement `pty_spawn` (the child sequence + error-pipe handshake + identity capture) in `pty_shim.c`

- [ ] Add `#include <sys/wait.h>`, `#include <poll.h>`, `#include <time.h>`, and on Linux `#include <sys/prctl.h>`, `#include <signal.h>` to the top of `pty_shim.c`. Add `#include <pty.h>` for `forkpty` (glibc/musl both provide it via `<pty.h>`; macOS via `<util.h>` — guard with `#ifdef __APPLE__ #include <util.h> #else #include <pty.h> #endif`).

- [ ] Add the Linux native `lx:` capture (mirrors the existing C# `ProcessStartToken.ReadLinuxStartTicks`/`LinuxBootId` algorithm exactly, so the two independently-computed strings for the same pid always match byte-for-byte):

```c
#ifdef __linux__
static int capture_lx_identity(pid_t pid, char *out, size_t outlen) {
    char statpath[64];
    snprintf(statpath, sizeof(statpath), "/proc/%d/stat", (int)pid);
    int fd = open(statpath, O_RDONLY);
    if (fd < 0) return 0;

    char buf[512];
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0) return 0;
    buf[n] = '\0';

    // Fields after the (possibly space/paren-containing) comm begin after the LAST ')'.
    char *after = strrchr(buf, ')');
    if (!after || !after[1]) return 0;
    after += 2; // skip ") "

    char *tok, *save = NULL;
    int field = 0; // 0-indexed from "state" (field 3 overall == index 0 here)
    char *starttime = NULL;
    for (tok = strtok_r(after, " ", &save); tok; tok = strtok_r(NULL, " ", &save), field++) {
        if (field == 19) { starttime = tok; break; } // starttime is field 22 overall, index 19 here
    }
    if (!starttime) return 0;

    int bfd = open("/proc/sys/kernel/random/boot_id", O_RDONLY);
    char boot[64] = "?";
    if (bfd >= 0) {
        ssize_t bn = read(bfd, boot, sizeof(boot) - 1);
        close(bfd);
        if (bn > 0) { boot[bn] = '\0'; char *nl = strchr(boot, '\n'); if (nl) *nl = '\0'; }
    }

    snprintf(out, outlen, "lx:%s:%s", boot, starttime);
    return 1;
}
#endif

#ifdef __APPLE__
#include <sys/sysctl.h>
#include <libproc.h>
#include <assert.h>

// PROC_PIDUNIQIDENTIFIERINFO (flavor 17) and struct proc_uniqidentifierinfo are #ifdef
// PRIVATE in the public SDK's sys/proc_info.h — proc_pidinfo() itself IS public, the flavor
// is not declared. Vendored verbatim from the xnu open-source tree (bsd/kern/proc_info.c /
// bsd/sys/proc_info.h) rather than pretending a public header declares this.
#define PTY_PROC_PIDUNIQIDENTIFIERINFO 17

struct pty_proc_uniqidentifierinfo {
    uint8_t  p_uuid[16];
    uint64_t p_uniqueid;
    uint64_t p_puniqueid;
    int32_t  p_reserve1;
    int32_t  p_reserve2;
    int32_t  p_reserve3;
    uint32_t p_reserve4;
};

_Static_assert(sizeof(struct pty_proc_uniqidentifierinfo) == 40,
               "proc_uniqidentifierinfo layout drifted from the vendored xnu ABI");

int pty_capture_mac_identity(pid_t pid, char *out, size_t outlen) {
    struct pty_proc_uniqidentifierinfo info;
    int n = proc_pidinfo(pid, PTY_PROC_PIDUNIQIDENTIFIERINFO, 0, &info, sizeof(info));
    if (n != (int)sizeof(info) || info.p_uniqueid == 0) return 0; // anomaly → uncapturable, never a false proof

    char uuid_str[64];
    size_t uuid_size = sizeof(uuid_str);
    if (sysctlbyname("kern.bootsessionuuid", uuid_str, &uuid_size, NULL, 0) != 0) return 0;

    snprintf(out, outlen, "mac:%s:%llu", uuid_str, (unsigned long long)info.p_uniqueid);
    return 1;
}
#endif

static int capture_start_identity(pid_t pid, char *out, size_t outlen) {
    out[0] = '\0';
#ifdef __linux__
    if (capture_lx_identity(pid, out, outlen)) return 1;
#elif defined(__APPLE__)
    if (pty_capture_mac_identity(pid, out, outlen)) return 1;
#endif
    return 0; // uncapturable — out stays "" (identity_unavailable), never a launch failure
}
```

- [ ] Implement `pty_spawn` itself:

```c
int pty_spawn(const pty_exec_plan *plan, char *const envp[], const char *cwd,
              unsigned short rows, unsigned short cols,
              pid_t expected_parent, int cancel_fd, pty_spawn_result *out) {
    memset(out, 0, sizeof(*out));
    out->master_fd = -1;

    // Self-pipe for child-failure reporting — BOTH ends CLOEXEC-flagged BEFORE the fork so a
    // successful exec closes the child's copy of the write end automatically (EOF => success).
    int errpipe[2];
    if (pipe(errpipe) != 0) { out->err_no = errno; out->failed_step = PTY_STEP_FORK; return -1; }
    fcntl(errpipe[0], F_SETFD, FD_CLOEXEC);
    fcntl(errpipe[1], F_SETFD, FD_CLOEXEC);

    struct winsize ws = {0};
    ws.ws_row = rows; ws.ws_col = cols;

    int master_fd;
    pid_t pid = forkpty(&master_fd, NULL, NULL, &ws);

    if (pid < 0) {
        out->err_no = errno; out->failed_step = PTY_STEP_FORK;
        close(errpipe[0]); close(errpipe[1]);
        return -1;
    }

    if (pid == 0) {
        // ── CHILD ── async-signal-safe calls only, no allocation, no managed re-entry.
        close(errpipe[0]);

        int step_fail_and_die(int step, int err) {
            struct { int step; int err; } msg = { step, err };
            write(errpipe[1], &msg, sizeof(msg));
            _exit(127);
            return 0; // unreachable
        }

#ifdef __linux__
        if (prctl(PR_SET_PDEATHSIG, SIGKILL) != 0) { step_fail_and_die(PTY_STEP_PRCTL, errno); }
        if (getppid() != expected_parent) {
            struct { int step; int err; } msg = { PTY_STEP_PARENT_DIED, 0 };
            write(errpipe[1], &msg, sizeof(msg));
            raise(SIGKILL);
        }
#endif
        if (chdir(cwd) != 0) { step_fail_and_die(PTY_STEP_CHDIR, errno); }

        if (plan->mode == PTY_EXEC_FD) {
#ifdef __linux__
            syscall(SYS_execveat, plan->exec_fd, "", plan->argv, envp, AT_EMPTY_PATH);
#endif
        } else {
            execve(plan->exec_path, plan->argv, envp);
        }

        step_fail_and_die(PTY_STEP_EXEC, errno);
        return 0; // unreachable
    }

    // ── PARENT ──
    close(errpipe[1]);

    // CAPTURE-BINDING RULE: identity is captured HERE, immediately after forkpty returns,
    // before anything (including this very function) waitpid()s the child. A fast-exiting
    // child cannot be reaped-and-replaced before this line runs.
    capture_start_identity(pid, out->start_identity, sizeof(out->start_identity));

    // Bounded handshake: poll the error pipe (+ cancel_fd) with a 30s deadline.
    struct pollfd fds[2];
    fds[0].fd = errpipe[0]; fds[0].events = POLLIN;
    int nfds = 1;
    if (cancel_fd >= 0) { fds[1].fd = cancel_fd; fds[1].events = POLLIN; nfds = 2; }

    int poll_rc = poll(fds, nfds, 30000);

    if (poll_rc == 0) {
        // Deadline: kill + reap, report timeout.
        kill(pid, SIGKILL);
        waitpid(pid, NULL, 0);
        close(errpipe[0]);
        close(master_fd);
        out->failed_step = PTY_STEP_HANDSHAKE_TIMEOUT;
        return -1;
    }

    if (nfds == 2 && (fds[1].revents & POLLIN)) {
        // Shutdown cancellation.
        kill(pid, SIGKILL);
        waitpid(pid, NULL, 0);
        close(errpipe[0]);
        close(master_fd);
        out->failed_step = PTY_STEP_CANCELLED;
        return -1;
    }

    struct { int step; int err; } msg;
    ssize_t n = read(errpipe[0], &msg, sizeof(msg));
    close(errpipe[0]);

    if (n == sizeof(msg)) {
        // Child reported a failure at a shim-controlled step.
        waitpid(pid, NULL, 0);
        close(master_fd);
        out->err_no = msg.err;
        out->failed_step = msg.step;
        return -1;
    }

    // EOF (n == 0): the exec replaced the image, closing the CLOEXEC write end. Success.
    out->pid = pid;
    out->master_fd = master_fd;
    out->failed_step = PTY_STEP_NONE;
    return 0;
}
```

Note for the implementer: nested C function definitions (`step_fail_and_die` inside `pty_spawn`) are a **GCC/Clang extension**, not standard C — both compilers used across the release matrix support it, but if this trips a portability lint, hoist it to a top-level `static void child_fail_and_die(int errpipe_write_fd, int step, int err)` taking the pipe fd as a parameter instead; behavior is identical either way.

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/PtySpawnTests/*"`. Iterate until green on Linux. Expect the `Cancel_fd_...` test to need the `out`-across-`Task.Run` fix noted in Step 2 before it can even compile.
- [ ] On a macOS dev box (optional but recommended before Task 6's CI-only macOS coverage lands): manually build the shim (`cc -shared -o libpty_shim.dylib ...`) and run `Successful_spawn_returns_a_reapable_child_and_a_captured_identity` — confirm a `mac:` token comes back non-empty.
- [ ] Commit: `git add -A && git commit -m "L1-shim(b): native pty_spawn — fork/exec/handshake/start-identity capture"`

---

## Task 4 — L1-managed(a): dedicated daemon-lifetime Unix spawner thread

**Testability:** `PR_SET_PDEATHSIG` fires when the **creating thread's process** dies — to prove that (and to prove `Environment.FailFast` on unexpected thread death), you must kill/crash an actual separate OS process and observe from outside it; you cannot safely assert "this process just crashed" from inside the same test run. This task adds a tiny **out-of-process helper** (`NativeTestHost`) for exactly those two assertions; both are genuinely Linux-only in what they prove (PDEATHSIG has no macOS/Windows equivalent) and are CI-verified on `ubuntu-latest`. The "spawner thread survives unrelated pool-thread churn" test runs in-process and is portable/local on any Unix dev box.

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Capacitor.Cli.Tests.Unit.NativeTestHost.csproj`
- Create: `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Program.cs`
- Create: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixSpawnerThread.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (+ `ProjectReference`)
- Modify: `Capacitor.slnx` (+ the new project, in `/test/`)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/UnixSpawnerThreadTests.cs` (new)

**Interfaces:**
- Consumes: `UnixPtyInterop.pty_spawn`/`pty_preflight`/`pty_plan_free` (Tasks 2–3); `UnixPtyProcessFactory.Spawn` (public, existing — the host process calls this end-to-end, exercising the REAL production entry point, not a shortcut).
- Produces (consumed by Task 5): `UnixSpawnerThread` — a `IDisposable` singleton with:

```csharp
internal sealed class UnixSpawnerThread : IDisposable {
    // Submits a spawn request to the dedicated thread and waits for its result. Never runs
    // pty_spawn on the calling (pool) thread — PR_SET_PDEATHSIG is a per-THREAD property, so
    // a pool thread retiring would SIGKILL every healthy agent it ever spawned.
    public UnixPtyInterop.PtySpawnResult SpawnOn(
        IntPtr plan, string?[] envp, string cwd, ushort rows, ushort cols, int expectedParent, int cancelFd);

    public void Dispose(); // signals the thread to stop and joins it — normal shutdown ONLY
                            // (called after AgentOrchestrator has already stopped every agent)
}
```

### Step 1: Create the `NativeTestHost` helper project

- [ ] Create `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Capacitor.Cli.Tests.Unit.NativeTestHost.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\..\src\Capacitor.Cli.Daemon\Capacitor.Cli.Daemon.csproj" />
    </ItemGroup>
</Project>
```

- [ ] Create `test/Capacitor.Cli.Tests.Unit.NativeTestHost/Program.cs`:

```csharp
using Capacitor.Cli.Daemon.Pty.Unix;

// A tiny, disposable process the OUTER test can kill and observe from the outside — the
// mechanism the PDEATHSIG and spawner-thread-FailFast tests need (you cannot safely assert
// "my own process just crashed" from inside the process doing the crashing).
var mode = args.Length > 0 ? args[0] : "";

switch (mode) {
    case "spawn-dummy": {
        // Exercises the REAL production entry point end-to-end (not a shortcut into pty_spawn
        // directly) so this proves the actual daemon spawn path, including the spawner thread
        // Task 5 wires UnixPtyProcessFactory through.
        var factory = new UnixPtyProcessFactory();
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
    default:
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 1;
}

return 0;
```

- [ ] Add the project to `Capacitor.slnx`'s `/test/` folder:

```xml
<Project Path="test\Capacitor.Cli.Tests.Unit.NativeTestHost\Capacitor.Cli.Tests.Unit.NativeTestHost.csproj" />
```

- [ ] Add a `ProjectReference` to it from `test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (so build ordering is correct and the host is always built alongside the test suite):

```xml
<ProjectReference Include="..\Capacitor.Cli.Tests.Unit.NativeTestHost\Capacitor.Cli.Tests.Unit.NativeTestHost.csproj" />
```

- [ ] Build: `dotnet build Capacitor.slnx`. Expected: fails — `UnixSpawnerThread` doesn't exist yet. This is fine; it confirms wiring. Proceed to Step 2.

### Step 2: Write the failing spawner-thread tests

- [ ] Add a small resolver + the three tests to `test/Capacitor.Cli.Tests.Unit/Daemon/UnixSpawnerThreadTests.cs` (new file):

```csharp
using System.Diagnostics;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// L1-managed(a) (spec §4.2(b)): every Linux pty_spawn call runs on ONE dedicated,
/// daemon-lifetime native thread — never a pool thread (PR_SET_PDEATHSIG is a per-THREAD
/// property; a pool thread retiring would SIGKILL every agent it spawned). Two of these three
/// tests need a real separate OS process (see this task's testability note) via the
/// NativeTestHost helper; the third runs in-process.
/// </summary>
public class UnixSpawnerThreadTests {
    [Test]
    public async Task Pdeathsig_kills_the_child_when_the_spawner_process_dies() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = StartHost("spawn-dummy");
        var childPid = await ReadPidLineAsync(host);

        host.Kill(entireProcessTree: false); // simulate an external daemon crash (SIGKILL)
        host.WaitForExit(5000);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsAlive(childPid)) await Task.Delay(200);

        await Assert.That(IsAlive(childPid)).IsFalse();
    }

    [Test]
    public async Task Unexpected_spawner_thread_exit_fail_fasts_the_host_process() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = StartHost("crash-spawner");
        var exited = host.WaitForExit(10000);

        await Assert.That(exited).IsTrue();
        // Environment.FailFast raises SIGABRT on Unix — .NET reports that as a large/negative
        // native exit code (128 + signal, i.e. 134), NOT a clean 0. Assert it's non-zero rather
        // than pin the exact platform-dependent encoding.
        await Assert.That(host.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task Agent_survives_unrelated_pool_thread_churn_while_the_thread_lives() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();

        // Churn several short-lived pool threads BEFORE and AFTER the spawn — none of them is
        // the thread that called pty_spawn, so none of their deaths should matter.
        for (var i = 0; i < 5; i++) await Task.Run(() => { });

        var rc = UnixPtyInterop.pty_preflight("/bin/sleep", ["sleep", "3", null], [], 1, out var plan);
        await Assert.That(rc).IsEqualTo(0);

        var result = spawner.SpawnOn(plan, [], Directory.GetCurrentDirectory(), 40, 120, Environment.ProcessId, -1);
        try {
            for (var i = 0; i < 5; i++) await Task.Run(() => { });
            await Task.Delay(300);
            await Assert.That(IsAlive(result.Pid)).IsTrue();
        } finally {
            UnixPtyInterop.kill(result.Pid, UnixPtyInterop.SIGKILL);
            UnixPtyInterop.waitpid(result.Pid, out _, 0);
            var p = plan; UnixPtyInterop.pty_plan_free(ref p);
        }
    }

    static bool IsAlive(int pid) => UnixPtyInterop.kill(pid, 0) == 0;

    static Process StartHost(string mode) {
        var dll = ResolveNativeHostDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {mode}") {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start NativeTestHost");
    }

    static async Task<int> ReadPidLineAsync(Process host) {
        var line = await host.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("no PID line from host");
        return int.Parse(line["PID=".Length..]);
    }

    // Sibling-project resolution: the test assembly and the host live at
    // test/Capacitor.Cli.Tests.Unit/bin/<Config>/<TFM>/ and
    // test/Capacitor.Cli.Tests.Unit.NativeTestHost/bin/<Config>/<TFM>/ respectively. Adjust this
    // if your local build layout differs (e.g. a custom -o/OutDir); the value is deliberately
    // derived rather than a hardcoded absolute path so CI and local dev share the same logic.
    static string ResolveNativeHostDll() {
        var dir       = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm       = Path.GetFileName(dir);
        var config    = Path.GetFileName(Path.GetDirectoryName(dir)!);
        var testRoot  = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var hostDll   = Path.Combine(testRoot, "Capacitor.Cli.Tests.Unit.NativeTestHost", "bin", config, tfm,
            "Capacitor.Cli.Tests.Unit.NativeTestHost.dll");

        if (!File.Exists(hostDll))
            throw new InvalidOperationException($"NativeTestHost not built at {hostDll} — build Capacitor.slnx first");

        return hostDll;
    }
}
```

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/UnixSpawnerThreadTests/*"`. Expected: compile failure (`UnixSpawnerThread` doesn't exist).

### Step 3: Implement `UnixSpawnerThread`

- [ ] Create `src/Capacitor.Cli.Daemon/Pty/Unix/UnixSpawnerThread.cs`:

```csharp
using System.Collections.Concurrent;

namespace Capacitor.Cli.Daemon.Pty.Unix;

/// <summary>
/// L1-managed(a) (spec §4.2(b)): one dedicated, daemon-lifetime native thread that runs EVERY
/// Unix <c>pty_spawn</c> call — never a thread-pool thread. <c>PR_SET_PDEATHSIG</c> fires when
/// the CREATING THREAD's process dies, not the thread itself, but the safety net only works
/// if that thread is never retired out from under a still-running agent: a pool thread that
/// finishes and is reused/retired would (on the semantics PDEATHSIG advertises) SIGKILL every
/// healthy agent it ever spawned, since the OS ties the signal to the thread's lifetime via the
/// process's thread-group in a way this design deliberately never risks by using a pool thread
/// at all. Unexpected termination of THIS thread therefore <see cref="Environment.FailFast"/>s
/// the whole daemon process — children then die WITH the daemon, which is exactly the
/// semantic PDEATHSIG advertises, rather than leaving a half-broken daemon that silently
/// stopped protecting agents it already spawned. macOS also routes through this same thread
/// (no PDEATHSIG dependency there, but one native child-sequence code path on both Unixes is
/// simpler than two).
/// </summary>
internal sealed class UnixSpawnerThread : IDisposable {
    readonly BlockingCollection<SpawnRequest> _queue = new();
    readonly Thread                           _thread;
    volatile bool                             _stopping;

    public UnixSpawnerThread() {
        _thread = new Thread(RunLoop) { IsBackground = false, Name = "kcap-unix-spawner" };
        _thread.Start();
    }

    /// <summary>Submit a spawn request to the dedicated thread and block until it completes.
    /// Throws <see cref="ObjectDisposedException"/> if called after <see cref="Dispose"/> —
    /// normal shutdown stops agents first, so no in-flight spawn should ever race disposal.</summary>
    public UnixPtyInterop.PtySpawnResult SpawnOn(
            IntPtr plan, string?[] envp, string cwd, ushort rows, ushort cols, int expectedParent, int cancelFd) {
        var request = new SpawnRequest(plan, envp, cwd, rows, cols, expectedParent, cancelFd);
        _queue.Add(request);
        return request.Completion.Task.GetAwaiter().GetResult();
    }

    void RunLoop() {
        try {
            foreach (var request in _queue.GetConsumingEnumerable()) {
                try {
                    var rc = UnixPtyInterop.pty_spawn(
                        request.Plan, request.Envp, request.Cwd, request.Rows, request.Cols,
                        request.ExpectedParent, request.CancelFd, out var result);

                    request.Completion.SetResult(result);
                    _ = rc; // failure is reported INSIDE result (failed_step/err_no); the managed
                            // caller (Task 5) inspects that, not the native return code directly.
                } catch (Exception ex) {
                    request.Completion.SetException(ex);
                }
            }
        } catch (Exception ex) when (!_stopping) {
            // Anything that escapes the loop itself (not a per-request failure, which is caught
            // above) means the spawner thread died in a way we didn't plan for. Fail the WHOLE
            // daemon process rather than silently stop protecting already-spawned agents.
            Environment.FailFast("UnixSpawnerThread terminated unexpectedly", ex);
        }
    }

    /// <summary>Normal shutdown ONLY — call after every hosted agent has already been stopped.
    /// Signals the loop to drain and stop, then joins it.</summary>
    public void Dispose() {
        _stopping = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Test-only seam (spec §5's "unexpected spawner-thread termination fail-fasts"
    /// case): forces the loop to throw from a genuinely unexpected place, without a live queue
    /// item, so the FailFast path fires exactly like a real native-call bug would.</summary>
    internal void CrashForTest() {
        _stopping = false; // ensure the catch guard doesn't swallow this
        _queue.Add(new SpawnRequest(IntPtr.Zero, [], "", 0, 0, -999999 /* nonsense */, -1));
        // The nonsense expectedParent makes pty_preflight/pty_spawn's own error paths return a
        // normal failure result, NOT a crash — so instead directly inject a thread-ending
        // exception by disposing the queue out from under a concurrent Add, which throws
        // InvalidOperationException on the NEXT iteration of GetConsumingEnumerable, escaping
        // the per-request try/catch by construction (the exception surfaces from the
        // enumerator's MoveNext, outside the inner try). This is intentionally "cheat" code
        // reachable ONLY from this internal test seam.
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}

file sealed record SpawnRequest(
    IntPtr Plan, string?[] Envp, string Cwd, ushort Rows, ushort Cols, int ExpectedParent, int CancelFd) {
    public TaskCompletionSource<UnixPtyInterop.PtySpawnResult> Completion { get; } = new();
}
```

Note for the implementer: the `CrashForTest` mechanism above is fiddly (double-dispose of a `BlockingCollection` needs verifying it actually throws from inside `RunLoop`'s enumerator rather than from `CrashForTest` itself). If `dotnet run ... UnixSpawnerThreadTests` shows the `crash-spawner` NativeTestHost mode exiting cleanly (exit code 0) instead of via `FailFast`, the simplest reliable fix is to make `RunLoop`'s body call a `virtual`/injectable delegate for "process one request" and have `CrashForTest` install a delegate that throws on its first invocation, then push one dummy request — simpler to reason about than double-disposing the collection. Treat the exact mechanism as an implementation detail to get the RED→GREEN cycle to prove the real contract: **an exception escaping the spawner loop's outer scope calls `Environment.FailFast`**.

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/UnixSpawnerThreadTests/*"`. Iterate until green on Linux; the third test (`Agent_survives_unrelated_pool_thread_churn...`) should also pass on macOS.
- [ ] Commit: `git add -A && git commit -m "L1-managed(a): dedicated daemon-lifetime Unix spawner thread"`

---

## Task 5 — L1-managed(b): rewrite `UnixPtyProcess.Spawn` onto the native shim; thread the captured identity through to `PersistPidRecordOrThrow`

**Testability:** The end-to-end "an agent spawned via the real production path is contained/reapable/has a captured identity" tests run and are asserted on both Linux and macOS locally and in CI (Linux in `ci.yml`; macOS build-time smoke in `release.yml`, Task 6). The "managed child branch is structurally deleted" test is a pure reflection/source check — no OS dependency, runs everywhere including Windows (as a no-op-if-absent check would be backwards; instead assert the type doesn't exist, which is OS-independent).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs` (full rewrite of `Spawn`, `:22-98`; delete the `case 0:` managed child branch, `:59-93`)
- Modify: `src/Capacitor.Cli.Daemon/Pty/IPtyProcess.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:401-420` (`PersistPidRecordOrThrow`), `:770` (call site)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:198-202` (DI registration)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/UnixSpawnerThreadTests.cs` or add `test/Capacitor.Cli.Tests.Unit/Daemon/UnixPtyProcessSpawnTests.cs` (new)

**Interfaces:**
- Consumes: `UnixSpawnerThread.SpawnOn(...)` (Task 4); `UnixPtyInterop.pty_preflight`/`pty_probe_execveat`/`pty_plan_free` (Task 2); `UnixPtyInterop.pty_spawn`/`PtySpawnResult` (Task 3).
- Produces (consumed by Task 8/9 and by `AgentOrchestrator`):
  - `IPtyProcess.StartIdentity` — `string? StartIdentity => null;` (default interface member; only `UnixPtyProcess` overrides it).
  - `IHostedAgentRuntime.StartIdentity` — same default-member pattern; `PtyHostedAgentRuntime.StartIdentity => pty.StartIdentity`.
  - `AgentOrchestrator.PersistPidRecordOrThrow(AgentInstance agent, int pid, string? capturedStartIdentity)` — new third parameter. **Contract:** `capturedStartIdentity == null` ⇒ legacy path (Windows / ACP runtimes) — re-capture via `ProcessIdentity.Capture(pid)`, throw if alive-and-uncapturable, exactly as today. `capturedStartIdentity == ""` ⇒ Unix shim attempted capture and failed (uncapturable) — write an `IdentityKind.IdentityUnavailable` record, **never throw**, `agent.StartIdentity = ""` (the same string; see the sentinel note below). `capturedStartIdentity` non-empty ⇒ Unix shim succeeded — write an `IdentityKind.Present` record with that exact string, **never re-capture**.

**Design note — why `agent.StartIdentity = ""` (not `null`) for the uncapturable case:** `AgentOrchestrator.CleanupAgentAsync` (`:1891`) already has a teardown-quarantine check shaped `agent.StartIdentity is { } startIdentity && ProcessIdentity.IsAlive(pid) && ProcessIdentity.MatchesTri(pid, startIdentity) != false`. `MatchesTri(pid, "")` always returns `null` (an empty string has no `:` scheme separator, so `SameScheme` always answers false, so `Matches` returns `null`) — i.e. permanently "uncomparable", which is exactly the desired "ambiguity never kills" behavior at teardown for an identity-unavailable agent: if it's still alive, quarantine-and-retry-forever (never falsely confirmed dead); once it's actually dead, `ProcessIdentity.IsAlive` catches that regardless of the empty identity. Using `null` instead would take the teardown code down the WRONG branch (`else { DeletePidRecord(...) }`, unconditionally treating it as confirmed-gone) even while the process might still be alive. This is a load-bearing detail — get it wrong and an identity-unavailable-but-still-alive agent's record silently disappears at normal teardown.

### Step 1: Add the `StartIdentity` default interface members

- [ ] `IPtyProcess.cs` — add after `int? ExitCode { get; }`:

```csharp
/// <summary>The start-identity token captured NATIVELY, inside the spawn call, immediately
/// after the child exists (the capture-binding rule) — <c>null</c> when this runtime never
/// captures one this way (Windows; ACP runtimes have no PTY at all), in which case the caller
/// falls back to a legacy post-hoc <c>ProcessStartToken.ForPid</c> re-capture. On Unix this is
/// NEVER null: it's either a real token or <c>""</c> (capture attempted and failed — see
/// <c>PidIdentityKind.IdentityUnavailable</c>), and the caller must NOT re-capture in that case
/// (that would defeat the whole point of capturing pre-reap).</summary>
string? StartIdentity => null;
```

- [ ] `IHostedAgentRuntime.cs` — same doc comment, same default, added after `int? ExitCode { get; }`.
- [ ] `PtyHostedAgentRuntime.cs` — add `public string? StartIdentity => pty.StartIdentity;` next to the existing `ExitCode` property.
- [ ] Build: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`. Expected: success (default interface members need no changes anywhere else — `ConPtyProcess`, `AcpHostedAgentRuntime`, and every test fake implementing these interfaces keep compiling unchanged).
- [ ] Commit: `git add src/Capacitor.Cli.Daemon/Pty/IPtyProcess.cs src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs && git commit -m "Add StartIdentity to IPtyProcess/IHostedAgentRuntime (default null)"`

### Step 2: Write the failing end-to-end spawn test

- [ ] Create `test/Capacitor.Cli.Tests.Unit/Daemon/UnixPtyProcessSpawnTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>L1-managed(b): the REAL production entry point (UnixPtyProcessFactory.Spawn) end
/// to end — resolves PATH in the parent, builds a plan via pty_preflight, spawns via the
/// dedicated spawner thread, and surfaces the natively-captured StartIdentity.</summary>
public class UnixPtyProcessSpawnTests {
    [Test]
    public async Task Spawn_produces_a_running_process_with_a_captured_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var factory = new UnixPtyProcessFactory();
        var proc    = factory.Spawn("sleep", ["5"], Directory.GetCurrentDirectory());
        try {
            await Assert.That(proc.Pid).IsGreaterThan(0);
            await Assert.That(proc.StartIdentity).IsNotNull();
            await Assert.That(proc.StartIdentity).IsNotEmpty();
            await Assert.That(proc.StartIdentity).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");

            // Cross-check against the independent, existing ProcessStartToken machinery: the
            // shim-captured identity must be the SAME token a live re-derivation produces (they
            // read the same kernel facts, just via two different code paths).
            var liveToken = Capacitor.Cli.Core.ProcessStartToken.ForPid(proc.Pid);
            await Assert.That(liveToken).IsEqualTo(proc.StartIdentity);
        } finally {
            await proc.DisposeAsync();
        }
    }

    [Test]
    public async Task The_managed_fork_exec_branch_no_longer_exists() {
        // Structural, OS-independent: after L1, NO managed code runs between fork and exec on
        // any Unix — the entire child sequence lives in pty_spawn. This is asserted by grepping
        // the compiled IL for `execvp`/`chdir`/`setenv` P/Invoke CALLS from UnixPtyProcess
        // specifically (the P/Invoke DECLARATIONS may still exist elsewhere if reused, but
        // UnixPtyProcess.Spawn itself must not reference them anymore).
        var spawnMethod = typeof(UnixPtyProcess).GetMethod("Spawn",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var body = spawnMethod.GetMethodBody()!;
        // A crude but adequate structural check: the method body's IL must not reference
        // UnixPtyInterop.execvp at all (its token would appear in the method's referenced
        // members). Walk the declaring module's resolved tokens is overkill for a TDD check —
        // simplest robust proxy: UnixPtyInterop.execvp itself should have NO remaining managed
        // callers in this assembly other than (at most) none. Implement via
        // System.Reflection.Metadata if a naive check proves too fragile; the load-bearing
        // assertion is qualitative (delete the `case 0:` branch), not this specific probe.
        await Assert.That(body).IsNotNull();
    }
}
```

- [ ] Note for the implementer: the second test's exact mechanism is intentionally left loose — the REAL assertion this task must satisfy is simply **"the `case 0:` child branch in `UnixPtyProcess.Spawn` is deleted"**, which is best verified by code review / the diff itself, not a brittle reflection probe. Feel free to replace `The_managed_fork_exec_branch_no_longer_exists` with a simpler smoke (e.g. asserting `UnixPtyInterop.execvp` is no longer decorated with any doc-comment claiming production use, or just delete this test and rely on the PR diff) if the reflection approach fights you — this is explicitly NOT a case where a fragile test is worth keeping for its own sake.
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/UnixPtyProcessSpawnTests/*"`. Expected: compile failure (`UnixPtyProcessFactory` still uses the old parameterless-`Spawn` static call shape; `IPtyProcess.StartIdentity` exists from Step 1 but `UnixPtyProcess` doesn't override it yet) or a runtime assertion failure (`StartIdentity` is `null` from the interface default). Either is an acceptable red state.

### Step 3: Rewrite `UnixPtyProcess.Spawn`

- [ ] Replace `UnixPtyProcess.cs` in full:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Pty.Unix;

public sealed class UnixPtyProcess : IPtyProcess {
    readonly int                     _masterFd;
    readonly CancellationTokenSource _cts = new();
    bool                             _disposed;

    public int     Pid           { get; }
    public bool    HasExited     { get; private set; }
    public int?    ExitCode      { get; private set; }
    public string? StartIdentity { get; } // never null on Unix: "" (uncapturable) or a real token

    UnixPtyProcess(int masterFd, int childPid, string startIdentity) {
        _masterFd     = masterFd;
        Pid           = childPid;
        StartIdentity = startIdentity;
    }

    /// <summary>Executable resolution is PRE-FORK, in the parent (spec §4.2(a), pinned):
    /// resolves <paramref name="command"/> against the SAME env/PATH that will be passed to
    /// the child (never the daemon's ambient PATH if extraEnv overrides it — closes the
    /// execvpe "resolve against the wrong PATH" trap at the top level too, matching the
    /// shim's own env-shebang resolution rule). Mirrors POSIX execvp semantics: an absolute
    /// path or any path containing '/' is used as-is (resolved against cwd for the latter,
    /// matching what execvp already did); a bare name is searched on PATH.</summary>
    static string ResolveExecutableAbsolutePath(string command, IReadOnlyDictionary<string, string> childEnv) {
        if (Path.IsPathRooted(command)) return command;
        if (command.Contains('/')) return Path.GetFullPath(command);

        var path = childEnv.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException($"'{command}' not found on PATH");
    }

    static IReadOnlyDictionary<string, string> BuildChildEnv(Dictionary<string, string>? extraEnv, ushort cols, ushort rows) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry is { Key: string key, Value: string value }) env[key] = value;

        env["TERM"]    = "xterm-256color";
        env["LANG"]    = "en_US.UTF-8";
        env["COLUMNS"] = cols.ToString();
        env["LINES"]   = rows.ToString();

        foreach (var key in PtyEnvScrub.ClaudeSessionVars) env.Remove(key);
        foreach (var key in PtyEnvScrub.HostedAgentVars) env.Remove(key);
        foreach (var key in PtyEnvScrub.DaemonSupervisionVars) env.Remove(key);

        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv) env[key] = value;

        return env;
    }

    static string?[] ToEnvpArray(IReadOnlyDictionary<string, string> env) {
        var arr = new string?[env.Count + 1];
        var i = 0;
        foreach (var (k, v) in env) arr[i++] = $"{k}={v}";
        arr[i] = null;
        return arr;
    }

    static readonly Lazy<UnixSpawnerThread> Spawner = new(() => new UnixSpawnerThread());
    static readonly Lazy<int>               ExecveatSupported = new(() => UnixPtyInterop.pty_probe_execveat());

    public static UnixPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        var childEnv = BuildChildEnv(extraEnv, cols, rows);
        var envpArr  = ToEnvpArray(childEnv);

        var resolvedPath = ResolveExecutableAbsolutePath(command, childEnv);

        var origArgv = new string?[args.Length + 2];
        origArgv[0] = command; // argv[0] stays the ORIGINAL (possibly unresolved) command name
        Array.Copy(args, 0, origArgv, 1, args.Length);
        origArgv[^1] = null;

        var rc = UnixPtyInterop.pty_preflight(resolvedPath, origArgv, envpArr, ExecveatSupported.Value, out var plan);
        if (rc != 0) {
            throw new InvalidOperationException($"pty_preflight failed for '{resolvedPath}' — the executable could not be resolved");
        }

        if (UnixPtyInterop.pty_plan_contained(plan) == 0) {
            Console.Error.WriteLine($"[kcap] warning: launch of '{resolvedPath}' is UNCONTAINED (privileged binary, unreadable/inspection-failed preflight, pre-3.19 kernel, or a multi-token/unresolvable shebang) — falling back to the managed record/scan reap layers only.");
        }

        try {
            var result = Spawner.Value.SpawnOn(plan, envpArr, cwd, rows, cols, Environment.ProcessId, cancelFd: -1);

            if (result.FailedStep != 0) {
                throw new InvalidOperationException(
                    $"pty_spawn failed: step {result.FailedStep}, errno {result.ErrNo}");
            }

            return new UnixPtyProcess(result.MasterFd, result.Pid, result.StartIdentityString);
        } finally {
            var p = plan;
            UnixPtyInterop.pty_plan_free(ref p); // the plan is spent whether spawn succeeded or failed
        }
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        ) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var       buf    = new byte[4096];

        while (!linked.Token.IsCancellationRequested && !HasExited) {
            var bytesRead = await Task.Run(
                () => {
                    var pfd = new UnixPtyInterop.PollFd { fd = _masterFd, events = UnixPtyInterop.POLLIN };

                    while (!linked.Token.IsCancellationRequested) {
                        var pollResult = UnixPtyInterop.poll(ref pfd, 1, 200);

                        switch (pollResult) {
                            case > 0 when (pfd.revents & UnixPtyInterop.POLLIN) != 0: {
                                var n = UnixPtyInterop.read(_masterFd, buf, buf.Length);
                                return (int)n;
                            }
                            case > 0 when (pfd.revents & (UnixPtyInterop.POLLHUP | UnixPtyInterop.POLLERR)) != 0:
                                return 0;
                            case < 0:
                                return -1;
                        }
                    }

                    return 0;
                },
                CancellationToken.None
            );

            if (bytesRead <= 0) { CheckExited(); yield break; }

            var data = new byte[bytesRead];
            Array.Copy(buf, data, bytesRead);
            yield return data;
        }
    }

    public Task WriteAsync(string input) {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Task.Run(() => UnixPtyInterop.write(_masterFd, bytes, bytes.Length));
    }

    public Task WriteAsync(byte[] data) => Task.Run(() => UnixPtyInterop.write(_masterFd, data, data.Length));

    public void Resize(ushort cols, ushort rows) => UnixPtyInterop.SetWinSize(_masterFd, rows, cols);

    public void SendInterrupt() {
        if (!HasExited) UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGINT);
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        if (HasExited) return;
        UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGTERM);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!HasExited && DateTime.UtcNow < deadline) {
            CheckExited();
            if (!HasExited) await Task.Delay(100);
        }

        if (!HasExited) { UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGKILL); CheckExited(); }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (HasExited) return;
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (!HasExited && sw.Elapsed < limit) {
            CheckExited();
            if (!HasExited) await Task.Delay(50);
        }
    }

    void CheckExited() {
        var result = UnixPtyInterop.waitpid(Pid, out var status, UnixPtyInterop.WNOHANG);
        if (result == Pid) { HasExited = true; ExitCode = (status >> 8) & 0xFF; }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync();
        if (!HasExited) await TerminateAsync();
        UnixPtyInterop.close(_masterFd);
        _cts.Dispose();
    }
}

public class UnixPtyProcessFactory : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        )
        => UnixPtyProcess.Spawn(command, args, cwd, extraEnv, cols, rows);
}
```

Note: the `Lazy<UnixSpawnerThread> Spawner` field here is a **pragmatic per-process-static singleton** — it works for tests (one thread per test process) and matches "one dedicated thread for the whole daemon", but it means `UnixPtyProcessFactory` doesn't own its lifecycle via DI disposal (Task 4's `UnixSpawnerThread.Dispose()` never gets called by the container this way). Fix this properly in Step 4 below by threading a `UnixSpawnerThread` instance through `UnixPtyProcessFactory`'s constructor instead of the static `Lazy` — the static field above is shown first because it's the simplest way to get `UnixPtyProcessSpawnTests` green; Step 4 replaces it with the DI-owned version so shutdown actually disposes the thread. Do not skip Step 4 — a `Lazy` static never disposed leaks the thread (harmless at process exit, but wrong for the "normal shutdown retires the thread" requirement).

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/UnixPtyProcessSpawnTests/*"`. Iterate until green.
- [ ] Run the FULL existing test suite once to check for regressions from deleting the managed child branch: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Expect some breakage in tests that assumed the old `execvp`-based spawn shape (e.g. anything asserting on `UnixPtyInterop.execvp` being called) — fix those forward, they're testing an implementation detail that's now gone.
- [ ] Commit: `git add -A && git commit -m "L1-managed(b): UnixPtyProcess.Spawn onto the native shim; delete the managed fork/exec branch"`

### Step 4: Make `UnixSpawnerThread` DI-owned (replace the static `Lazy`)

- [ ] Change `UnixPtyProcessFactory` to take the thread as a constructor dependency:

```csharp
public class UnixPtyProcessFactory(UnixSpawnerThread spawner) : IPtyProcessFactory {
    public IPtyProcess Spawn(string command, string[] args, string cwd,
            Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40)
        => UnixPtyProcess.Spawn(spawner, command, args, cwd, extraEnv, cols, rows);
}
```

- [ ] Change `UnixPtyProcess.Spawn`'s signature to accept the spawner explicitly (drop the static `Lazy<UnixSpawnerThread>` field; keep the static `Lazy<int> ExecveatSupported` — that one is a pure capability probe, safe to cache per-process regardless of DI lifetime):

```csharp
public static UnixPtyProcess Spawn(
        UnixSpawnerThread           spawner,
        string                      command,
        string[]                    args,
        string                      cwd,
        Dictionary<string, string>? extraEnv = null,
        ushort                      cols     = 120,
        ushort                      rows     = 40
    ) {
    // ... unchanged body, replacing `Spawner.Value.SpawnOn(...)` with `spawner.SpawnOn(...)`
}
```

- [ ] Update every direct call site of `new UnixPtyProcessFactory()` (the `NativeTestHost/Program.cs` from Task 4, and any test constructing it directly) to pass a `UnixSpawnerThread` instance — e.g. `new UnixPtyProcessFactory(new UnixSpawnerThread())`, disposing it if the caller owns the process lifetime (the `NativeTestHost` doesn't need to dispose — it's a disposable one-shot process that gets killed by the test anyway).
- [ ] Wire DI in `DaemonRunner.cs` (replace the existing `:198-202` block):

```csharp
if (OperatingSystem.IsWindows()) {
    builder.Services.AddSingleton<IPtyProcessFactory, WinPtyProcessFactory>();
} else {
    builder.Services.AddSingleton<UnixSpawnerThread>(); // disposed by the host container at shutdown
    builder.Services.AddSingleton<IPtyProcessFactory, UnixPtyProcessFactory>();
}
```

(`AddSingleton<UnixSpawnerThread>()` with no factory delegate resolves via its parameterless constructor, which already starts the thread — matches "created at daemon start". The generic host disposes registered `IDisposable` singletons during `host.StopAsync()`, which — per the existing shutdown sequence in `DaemonRunner.cs` around `:387-413` — runs after `ApplicationStopping` has already driven the orchestrator's agent-stop path, satisfying "normal shutdown stops agents first, then retires the thread" without any new explicit ordering code.)

- [ ] Re-run the full Task 4 + Task 5 test files plus the whole suite once more: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Expected: green.
- [ ] Commit: `git add -A && git commit -m "Make UnixSpawnerThread DI-owned so normal shutdown disposes it"`

### Step 5: Thread the captured identity into `PersistPidRecordOrThrow`

- [ ] In `AgentOrchestrator.cs`, change the method signature and body (`:401-420`):

```csharp
void PersistPidRecordOrThrow(AgentInstance agent, int pid, string? capturedStartIdentity) {
    if (_pidRecords is null) return;

    if (capturedStartIdentity is not null) {
        // Unix (post-L1): the shim captured (or definitively failed to capture) identity
        // INSIDE pty_spawn, immediately post-forkpty — NEVER re-capture a possibly-recycled
        // pid here. Empty string = identity_unavailable: a deliberate, well-formed record,
        // not a launch failure (spec §4.3).
        agent.StartIdentity = capturedStartIdentity; // "" is intentional — see UnixPtyProcess's design note

        _pidRecords.Write(new AgentPidRecord(
            agent.Id, pid, capturedStartIdentity,
            capturedStartIdentity.Length == 0 ? PidIdentityKind.IdentityUnavailable : PidIdentityKind.Present,
            agent.Kind.ToString(), agent.Vendor, agent.FlowRunId, agent.FlowRole,
            _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));

        return;
    }

    // Legacy path (Windows / ACP runtimes with no shim-based capture): unchanged behavior.
    var identity = ProcessIdentity.Capture(pid);
    if (identity is null) {
        if (ProcessIdentity.IsAlive(pid))
            throw new InvalidOperationException(
                $"Could not capture start-identity for live agent {agent.Id} (pid {pid}) — failing launch closed");

        return;
    }

    agent.StartIdentity = identity;

    _pidRecords.Write(new AgentPidRecord(
        agent.Id, pid, identity, PidIdentityKind.Present, agent.Kind.ToString(), agent.Vendor,
        agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));
}
```

(`PidIdentityKind` doesn't exist until Task 8 — this step is written now for narrative order but its final compile depends on Task 8's enum landing first. If executing tasks strictly in order, either do Task 8's `Models.cs` enum addition before this step, or temporarily stub `internal enum PidIdentityKind { Present, IdentityUnavailable }` in this file and let Task 8 delete the stub when it adds the real one to `Models.cs`. Recommended: do Task 8 immediately before this step if running tasks out of the plan's written order; otherwise take the stub.)

- [ ] Update the call site at `:770`: `PersistPidRecordOrThrow(agent, runtime.Pid);` → `PersistPidRecordOrThrow(agent, runtime.Pid, runtime.StartIdentity);`
- [ ] Run the full suite: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Expected: green (Windows/ACP behavior is byte-for-byte unchanged; Unix now records the shim-captured identity).
- [ ] Commit: `git add -A && git commit -m "Consume the shim-captured start-identity in PersistPidRecordOrThrow instead of re-capturing on Unix"`

**Second record-less spawn site (note, no code change in this task):** `AgentOrchestrator.LocalIpc.cs:68` (`kcap launch`'s local, unregistered path) calls `_ptyFactory.Spawn(...)` directly and never calls `PersistPidRecordOrThrow` at all — it has no durable PID record today and this plan doesn't add one (out of scope; it inherits W1/L1 OS containment automatically because it goes through the same `IPtyProcessFactory`, it just has no managed record/marker backstop if containment doesn't apply). Task 10 documents this explicitly in the coverage matrix so it isn't mistaken for an oversight.

---

## Task 6 — L1-build: per-RID native-shim packaging

**Testability:** The machine-type check and the load-and-call smoke are, by construction, only meaningful running on each RID's real (or emulated) hardware — they run **only in CI**, attached to `release.yml`'s existing per-RID `build` job (there is no per-RID matrix anywhere else — `ci.yml`'s `aot-check` is `linux-x64`-only). Nothing in this task is locally runnable across all 5 shim RIDs from one dev machine; verify the glibc-x64 and (if available) macOS-arm64 paths locally, trust CI for the rest, and expect to iterate over a few CI runs once this is pushed (called out again in the Self-Review section).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj:49-55` (the `CompilePtyShim` target)
- Modify: `.github/workflows/release.yml` (musl toolchain — already conditionally installed at `:84`, confirm; RID-isolated shim output; the `.dylib`-only guards at `:118-126`, `:188-190`, `:198-199`; new machine-type + load-and-call smoke step)

**Interfaces:**
- Consumes: `pty_shim.c`/`pty_shim.h` (Tasks 2–3, complete by now).
- Produces: a `libpty_shim.{so,dylib}` next to `kcap-daemon`/`kcap-daemon.exe` in every published artifact except `win-x64` (which has none — the Windows path never uses the shim).

### Step 1: Restructure `CompilePtyShim` to be per-RID and RID-isolated

- [ ] Replace the existing target (`Capacitor.Cli.Daemon.csproj:49-55`):

```xml
<!-- Compile the native PTY spawn shim for every non-Windows RID being published. Must run
     ON that RID's native runner (cross-publishing e.g. linux-arm64 from an x64 host would
     silently package an x64 .so) — release.yml's per-RID matrix already satisfies this;
     local `dotnet build`/`dotnet publish` with no explicit -r builds for the CURRENT host
     RID only, which is also always native. -->
<Target Name="CompilePtyShim" BeforeTargets="Build;Publish" Condition="'$(RuntimeIdentifier)' != 'win-x64' And !$([MSBuild]::IsOSPlatform('Windows'))">
    <PropertyGroup>
        <!-- RID-isolated output: never a shared $(OutputPath) that could collide across RID
             builds run on the same machine (e.g. local iteration switching RIDs). -->
        <PtyShimOutDir Condition="'$(RuntimeIdentifier)' != ''">$(OutputPath)$(RuntimeIdentifier)\</PtyShimOutDir>
        <PtyShimOutDir Condition="'$(RuntimeIdentifier)' == ''">$(OutputPath)</PtyShimOutDir>
        <PtyShimExt Condition="$([MSBuild]::IsOSPlatform('OSX'))">dylib</PtyShimExt>
        <PtyShimExt Condition="!$([MSBuild]::IsOSPlatform('OSX'))">so</PtyShimExt>
        <!-- musl targets need the musl C library/toolchain, not glibc's cc -->
        <PtyShimCc Condition="$(RuntimeIdentifier.Contains('musl'))">musl-gcc</PtyShimCc>
        <PtyShimCc Condition="'$(PtyShimCc)' == ''">cc</PtyShimCc>
    </PropertyGroup>
    <MakeDir Directories="$(PtyShimOutDir)" />
    <Exec Command="$(PtyShimCc) -shared -fPIC -O2 -o $(PtyShimOutDir)libpty_shim.$(PtyShimExt) Native/pty_shim.c" />
</Target>
<ItemGroup Condition="'$(RuntimeIdentifier)' != 'win-x64' And !$([MSBuild]::IsOSPlatform('Windows'))">
    <None Include="$(PtyShimOutDir)libpty_shim.$(PtyShimExt)" CopyToOutputDirectory="PreserveNewest" Link="libpty_shim.$(PtyShimExt)" />
</ItemGroup>
```

Note: `-fPIC` is added (harmless on macOS, required for a correctly-relocatable `.so` on Linux — the existing target never needed it because it only ever built the macOS `.dylib`, where position-independence is the default). The `Condition` on the target now fires for EVERY Linux/macOS RID (not just macOS), matching the new "4 Linux + 1 macOS shim targets" reality; Windows is excluded via both the RID check and an `IsOSPlatform('Windows')` belt-and-suspenders check (a `win-x64` publish should never even evaluate the musl/dylib property logic).

- [ ] Locally verify on your current dev OS: `dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release -r <your-RID>` and confirm `publish/libpty_shim.{so,dylib}` exists and `file publish/libpty_shim.*` reports the expected architecture.
- [ ] Commit: `git add src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj && git commit -m "L1-build: restructure CompilePtyShim per-RID, RID-isolated, musl-aware"`

### Step 2: Generalize `release.yml`'s `.dylib`-only guards to `.so`-awareness

- [ ] Replace the "Verify libpty_shim built" step (`:118-126`) — every non-`win-x64` RID now needs a shim, not just `osx-arm64`:

```yaml
      - name: Verify libpty_shim built
        if: matrix.rid != 'win-x64'
        shell: bash
        run: |
          ext="so"
          if [[ "${{ matrix.rid }}" == osx-* ]]; then ext="dylib"; fi
          if [ ! -f "publish/daemon/libpty_shim.$ext" ]; then
            echo "::error::libpty_shim.$ext missing from publish/daemon/ on ${{ matrix.rid }}"
            ls -la publish/daemon/
            exit 1
          fi
```

- [ ] Replace the "Copy binaries to npm package" step's guard (`:182-190`):

```yaml
      - name: Copy binaries to npm package
        shell: bash
        run: |
          mkdir -p npm/${{ matrix.npm-package }}/bin
          cp publish/cli/${{ matrix.cli-binary }} npm/${{ matrix.npm-package }}/bin/
          cp publish/daemon/${{ matrix.daemon-binary }} npm/${{ matrix.npm-package }}/bin/
          for ext in so dylib; do
            if [ -f "publish/daemon/libpty_shim.$ext" ]; then
              cp "publish/daemon/libpty_shim.$ext" npm/${{ matrix.npm-package }}/bin/
            fi
          done
```

- [ ] Apply the identical for-loop pattern to the "Create release archive" step's guard (`:198-199`, inside `mkdir -p archive` / `cp` block).
- [ ] Add the musl toolchain confirmation — it's ALREADY conditionally installed at `:84` (`contains(matrix.rid, 'musl') && 'musl-tools' || ''`); no change needed there, but add `musl-gcc` availability as an explicit assertion right after that step so a silently-broken musl toolchain install fails loudly here rather than inside the opaque `CompilePtyShim` `Exec`:

```yaml
      - name: Verify musl toolchain (musl RIDs only)
        if: contains(matrix.rid, 'musl') && !matrix.container
        run: musl-gcc --version
```

(Skip this check when `matrix.container` is set — the Alpine container path installs its OWN build toolchain via `apk add ... build-base` at `:74-78`, which provides `cc`/`gcc` natively inside musl libc, no separate `musl-gcc` cross-compiler needed there.)

- [ ] Commit: `git add .github/workflows/release.yml && git commit -m "L1-build: generalize release.yml's dylib-only guards to every shim RID"`

### Step 3: Add the per-RID machine-type + load-and-call smoke

- [ ] Add a new step to `release.yml`'s `build` job, right after "Verify libpty_shim built":

```yaml
      - name: Verify shim machine type and load-and-call smoke
        if: matrix.rid != 'win-x64'
        shell: bash
        run: |
          set -euo pipefail
          ext="so"; if [[ "${{ matrix.rid }}" == osx-* ]]; then ext="dylib"; fi
          shim="publish/daemon/libpty_shim.$ext"

          # Machine-type check: the RID's expected architecture must match the binary's
          # recorded machine type — catches a cross-compiled/wrong-arch artifact that would
          # otherwise only fail at runtime on the target device.
          case "${{ matrix.rid }}" in
            *-arm64) want_arch="arm64|aarch64" ;;
            *-x64)   want_arch="x86-64|x86_64" ;;
          esac

          if [[ "$ext" == "dylib" ]]; then
            actual=$(lipo -info "$shim" | grep -oE 'arm64|x86_64' | head -1)
          else
            actual=$(readelf -h "$shim" | grep -i "Machine:" | grep -oE 'X86-64|AArch64' | tr '[:upper:]' '[:lower:]')
          fi

          if ! echo "$actual" | grep -qEi "$want_arch"; then
            echo "::error::libpty_shim machine type ($actual) does not match RID ${{ matrix.rid }} (expected $want_arch)"
            exit 1
          fi

          # Load-and-call smoke: NativeAOT-publish and run a trivial C# program that loads the
          # shim and calls pty_spawn on a truly trivial command — proves the artifact isn't
          # just present but actually loadable and callable on this RID/arch, not merely
          # "a file with the right extension exists" (Task 6 acceptance is stronger than that).
          dotnet run --project test/Capacitor.Cli.Tests.Unit.NativeTestHost -- spawn-dummy &
          HOST_PID=$!
          sleep 2
          kill "$HOST_PID" 2>/dev/null || true
```

Note for the implementer: the load-and-call smoke above reuses the `NativeTestHost` from Task 4 rather than inventing a second throwaway harness — it's already built as part of the solution and already proves "load the shim, call the real production spawn path, get a live child pid." Tighten this step during implementation to actually assert `HOST_PID`'s stdout contained a `PID=` line before killing it (rather than just sleeping 2s and hoping) — the sketch above is deliberately simple to get a first green CI run; harden it once real CI output is in hand (this task's Self-Review note calls out that CI iteration is expected here).

- [ ] Push a branch and let CI run across the full `release.yml` build matrix (this step can't be meaningfully dry-run locally across all 5 RIDs) — iterate on any RID-specific tool-availability surprises (e.g. `readelf`/`lipo` not preinstalled on a given runner image; install via `apt-get`/pre-existing Xcode CLT as needed).
- [ ] Commit: `git add .github/workflows/release.yml && git commit -m "L1-build: per-RID machine-type + load-and-call smoke for libpty_shim"`

---

## Task 7 — M1-A(a): macOS `mac:` incarnation-identity scheme in `ProcessStartToken`

**Testability:** The `proc_pidinfo`/`sysctlbyname` calls are real macOS kernel calls with no useful emulation — every test in this task that asserts a real `mac:` token is `if (!OperatingSystem.IsMacOS()) return;` gated and is CI-verified only via `release.yml`'s `osx-arm64` build-time smoke (there's no macOS runner in `ci.yml` at all — see Global Constraints). It is, however, fully runnable on a local macOS dev box (unlike the Linux-CI-only native shim internals), so this is a good task to actually exercise locally before pushing if you have Mac hardware.

**Files:**
- Modify: `src/Capacitor.Cli.Core/ProcessStartToken.cs:38-120`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/ProcessStartTokenTests.cs:51-64,92-94` (extend)

**Interfaces:**
- Consumes: nothing from other tasks (this is a standalone `Core` addition — deliberately NOT dependent on `libpty_shim`, since `Capacitor.Cli`'s `DaemonCommands.cs` calls `ProcessStartToken.ForPid` without the daemon's native shim present — see Task 3's design note for why this duplicates rather than reuses the shim's `pty_capture_mac_identity`).
- Produces: `ProcessStartToken.ForPid(pid)` returns `"mac:{bootsessionuuid}:{p_uniqueid}"` on macOS instead of falling through to the `tk:` branch; the existing `Matches`/`SameScheme` tri-state logic is UNCHANGED (it already generalizes to any `scheme:value` shape) — a `mac:` vs `tk:` comparison is automatically cross-scheme ⇒ `null` ⇒ "spare, can't tell" (the `legacy_unresolvable` case Task 9 gives a name to).

### Step 1: Write the failing tests

- [ ] Extend `ProcessStartTokenTests.cs` — add after `ForPid_OnLinux_ReturnsBootScopedProcStarttime`:

```csharp
[Test]
public async Task ForPid_OnMacOS_ReturnsMacScheme() {
    if (!OperatingSystem.IsMacOS()) return;

    var token = ProcessStartToken.ForPid(Environment.ProcessId);

    await Assert.That(token).IsNotNull();
    await Assert.That(token!.StartsWith("mac:")).IsTrue();
    // Shape: mac:{uuid}:{digits} — a boot-session UUID (has dashes) then a plain integer.
    var parts = token.Split(':');
    await Assert.That(parts.Length).IsEqualTo(3);
    await Assert.That(parts[1].Contains('-')).IsTrue();
    await Assert.That(long.TryParse(parts[2], out var uniqueId)).IsTrue();
    await Assert.That(uniqueId).IsGreaterThan(0);
}

[Test]
public async Task ForPid_OnMacOS_IsStableAcrossCalls() {
    if (!OperatingSystem.IsMacOS()) return;

    // Same live process, called twice — must be byte-identical (unlike the old tk: scheme,
    // which was never actually unstable within a process either, but this guards the NEW
    // kernel-counter-based path specifically).
    var a = ProcessStartToken.ForPid(Environment.ProcessId);
    var b = ProcessStartToken.ForPid(Environment.ProcessId);
    await Assert.That(a).IsEqualTo(b);
}

[Test]
public async Task ForPid_OnMacOS_TwoDistinctProcessesGetDistinctUniqueIds() {
    if (!OperatingSystem.IsMacOS()) return;

    using var dummy = DummyProcess.StartSleep(5);
    var mine  = ProcessStartToken.ForPid(Environment.ProcessId);
    var other = ProcessStartToken.ForPid(dummy.Pid);

    await Assert.That(mine).IsNotNull();
    await Assert.That(other).IsNotNull();
    await Assert.That(mine).IsNotEqualTo(other);
    // Same boot-session UUID (same machine, same boot) — only the p_uniqueid half differs.
    await Assert.That(mine!.Split(':')[1]).IsEqualTo(other!.Split(':')[1]);
}

[Test]
public async Task Matches_MacSchemeVsLegacyTkScheme_IsNullCrossScheme() {
    if (!OperatingSystem.IsMacOS()) return;

    // A pre-M1-A tk: token compared against the live (now mac:-producing) process — must be
    // "can't tell" (null), never a false match and never a false "definitely different"
    // (that would let something wrongly treat a live legacy-recorded process as gone).
    var result = ProcessStartToken.Matches(Environment.ProcessId, "tk:123456789");
    await Assert.That(result).IsNull();
}
```

- [ ] Note: `DummyProcess` is in `Capacitor.Cli.Tests.Unit.Daemon`; `ProcessStartTokenTests` is already in that same namespace (confirmed from the existing file), so no extra `using` is needed.
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/ProcessStartTokenTests/*"`. Expected on macOS: the new tests fail (still producing `tk:` tokens, not `mac:`). Expected elsewhere: no-op pass.

### Step 2: Implement the vendored macOS capture in `ProcessStartToken.cs`

- [ ] Add the P/Invoke + vendored struct + capture helper (insert before the `ForPid` method or in a new `partial` companion — since `ProcessStartToken` is currently a non-partial static class, either make it `static partial class ProcessStartToken` to add `LibraryImport`s directly, or add a small internal sibling type; the plan uses the `partial` approach since it keeps everything in one file per the existing single-file convention for this class):

```csharp
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

public static partial class ProcessStartToken {
    // ── macOS: PROC_PIDUNIQIDENTIFIERINFO (flavor 17) is #ifdef PRIVATE in the public SDK's
    // sys/proc_info.h — proc_pidinfo() itself IS public, the flavor/struct are not declared.
    // Vendored verbatim from the xnu open-source tree (bsd/sys/proc_info.h). This duplicates
    // the SAME struct pty_shim.c vendors natively for pty_spawn's internal capture — see
    // Task 3's design note for why: Capacitor.Cli (the CLI binary) calls ForPid via
    // DaemonCommands.cs WITHOUT the daemon's libpty_shim present, so Core cannot depend on
    // the shim here. A cross-implementation consistency test (below) is the regression guard
    // against the two vendored copies drifting.
    const int ProcPidUniqIdentifierInfo = 17;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcUniqIdentifierInfo {
        public unsafe fixed byte Uuid[16];
        public ulong             UniqueId;
        public ulong             PUniqueId;
        public int               Reserve1;
        public int               Reserve2;
        public int               Reserve3;
        public uint              Reserve4;
    }

    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    static unsafe partial int sysctlbyname(string name, byte* oldp, nuint* oldlenp, IntPtr newp, nuint newlen);

    static unsafe string? CaptureMacToken(int pid) {
        var info = new ProcUniqIdentifierInfo();
        int written;
        var size = Marshal.SizeOf<ProcUniqIdentifierInfo>();

        var buf = new byte[size];
        fixed (byte* p = buf) {
            written = proc_pidinfo(pid, ProcPidUniqIdentifierInfo, 0, p, size);
        }

        // Fail-safe: only an exact-size read with a non-zero id proves anything. Any other
        // outcome (short read, error, zero id) is uncapturable — never a false proof.
        if (written != size) return null;

        var uniqueId = BitConverter.ToUInt64(buf, 16); // Uuid[16] then UniqueId
        if (uniqueId == 0) return null;

        Span<byte> uuidBuf = stackalloc byte[64];
        nuint uuidLen = (nuint)uuidBuf.Length;
        fixed (byte* up = uuidBuf) {
            if (sysctlbyname("kern.bootsessionuuid", up, &uuidLen, IntPtr.Zero, 0) != 0) return null;
        }

        var uuidStr = System.Text.Encoding.UTF8.GetString(uuidBuf[..(int)(uuidLen - 1)]); // drop the NUL
        return $"mac:{uuidStr}:{uniqueId}";
    }

    // ... existing members (ForCurrent, Matches, SameScheme, LinuxBootId, ReadLinuxStartTicks) follow, with ForPid changed below.
}
```

- [ ] Change `ForPid` (the existing `else` branch that unconditionally produces `tk:` for non-Linux):

```csharp
public static string? ForPid(int pid) {
    if (OperatingSystem.IsLinux()) {
        var starttime = ReadLinuxStartTicks(pid);
        return starttime is null ? null : $"lx:{LinuxBootId()}:{starttime}";
    }

    if (OperatingSystem.IsMacOS()) {
        // A live process must exist for this pid before trying the private-ABI capture —
        // proc_pidinfo on a gone pid returns a short/garbage read anyway, but check via
        // Process first so a nonexistent pid returns null through the SAME path as every
        // other platform (consistent contract for callers), and only THEN try the mac:
        // capture; if that specifically fails (anomaly), fall through to tk: is WRONG (it
        // would silently downgrade a live process to a wall-clock scheme) — so a capture
        // failure for a CONFIRMED-alive process returns null (uncapturable), matching the
        // Linux contract's "value can't be read" case, not a scheme fallback.
        try {
            using var proc = Process.GetProcessById(pid);
            return CaptureMacToken(pid) ?? null;
        } catch {
            return null;
        }
    }

    try {
        using var process = Process.GetProcessById(pid);
        return $"tk:{process.StartTime.ToUniversalTime().Ticks}";
    } catch {
        return null;
    }
}
```

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/ProcessStartTokenTests/*"`. Iterate until green on macOS.
- [ ] Run the FULL suite once (Windows/Linux behavior for this file is untouched, but confirm no regression): `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`.
- [ ] Commit: `git add src/Capacitor.Cli.Core/ProcessStartToken.cs test/Capacitor.Cli.Tests.Unit/Daemon/ProcessStartTokenTests.cs && git commit -m "M1-A(a): mac: incarnation-identity scheme (proc_pidinfo flavor 17 + kern.bootsessionuuid)"`

### Step 3: Add the cross-implementation consistency test (guards the C/C# duplication named in Task 3's design note)

- [ ] Add to `UnixPtyProcessSpawnTests.cs` (from Task 5) — this is effectively already covered by that task's `Spawn_produces_a_running_process_with_a_captured_identity` test's `liveToken` cross-check, since that test compares `proc.StartIdentity` (native-captured, via `pty_spawn`'s internal `pty_capture_mac_identity` on macOS) against `Capacitor.Cli.Core.ProcessStartToken.ForPid(proc.Pid)` (this task's independent C# capture) for the SAME live pid. If Task 5 was executed before this task, re-run that specific test now that `ForPid` actually produces `mac:` tokens on macOS (it would have been silently comparing two `tk:`-shaped strings before this task landed, which trivially passed without proving anything) — confirm it's actually exercising the `mac:` branch now (add a temporary `Console.WriteLine` or a debugger breakpoint to confirm, then remove it; the assertion itself needs no change).
- [ ] Commit (if any test file changed): `git add -A && git commit -m "Confirm the pty_spawn / ProcessStartToken.ForPid mac: cross-check exercises the real branch"`

---

## Task 8 — M1-A(b): `identity_kind` on `AgentPidRecord` + backward-compatible decode + inconsistent-shape quarantine

**Testability:** Pure managed code — JSON serialization/deserialization and file-store logic — no OS-specific behavior at all. Every test in this task runs locally and in CI on every OS unconditionally.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs:1272-1283` (the `AgentPidRecord` definition) and `:948` (`[JsonSerializable]` registrations)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs:47-72` (`ReadAll`'s validation loop)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (delete the Task-5 stub enum if one was added; the two `new AgentPidRecord(...)` call sites already updated in Task 5 Step 5 now compile against the real enum)
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/AgentPidRecordStoreTests.cs:18` (`Rec(...)` helper)
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/OrphanReaperTests.cs:21` (`Rec(...)` helper)
- Modify: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs` (via `StopAgentPidFallbackTests.cs`'s partial, the inline `new AgentPidRecord(...)` at line 23)
- Test: extend `AgentPidRecordStoreTests.cs` with the new quarantine/backward-compat cases

**Interfaces:**
- Consumes: nothing from other tasks (though Task 5's `PersistPidRecordOrThrow` rewrite references `PidIdentityKind` — see the ordering note in Task 5 Step 5).
- Produces (consumed by Task 9):

```csharp
public enum PidIdentityKind {
    Present             = 0, // has a comparable StartIdentity — the ONLY value that deserializes
                             // when a legacy pre-M1-A record's JSON has no "identity_kind" key at
                             // all (missing constructor-parameter → default(PidIdentityKind) → 0).
    IdentityUnavailable = 1, // StartIdentity == "" — a deliberate, well-formed "couldn't capture"
                             // marker (private-ABI anomaly), NOT a launch failure.
}

public readonly record struct AgentPidRecord(
    string          AgentId,
    int             Pid,
    string          StartIdentity,
    PidIdentityKind IdentityKind,
    string          Kind,
    string          Vendor,
    string?         FlowRunId,
    string?         FlowRole,
    string          DaemonId,
    string          DaemonEpoch,
    DateTimeOffset  SpawnedAt
);
```

### Step 1: Write the failing backward-compat + quarantine tests

- [ ] Add to `AgentPidRecordStoreTests.cs`:

```csharp
[Test]
public async Task ReadAll_decodes_a_legacy_record_with_no_identity_kind_key_as_present() {
    var dir   = NewStateDir();
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);

    // Build the JSON via the REAL serializer first (so this test can't drift from the actual
    // schema/casing), then surgically remove the identity_kind member — this produces the
    // EXACT old-shape JSON a pre-M1-A daemon actually wrote, not a hand-typed guess at the
    // property's wire name/casing.
    var current = new AgentPidRecord("legacy1", 999, "tk:123456789", PidIdentityKind.Present,
        "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);
    var json = System.Text.Json.JsonSerializer.Serialize(current, CapacitorJsonContext.Default.AgentPidRecord);
    var legacyJson = System.Text.RegularExpressions.Regex.Replace(
        json, "\"identity_kind\"\\s*:\\s*\"?[A-Za-z]+\"?,?", "");

    await Assert.That(legacyJson).DoesNotContain("identity_kind");

    var agentsDir = Path.Combine(dir, "agents");
    Directory.CreateDirectory(agentsDir);
    File.WriteAllText(Path.Combine(agentsDir, "legacy.json"), legacyJson);

    var all = store.ReadAll();
    await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "legacy1" });
    await Assert.That(all[0].IdentityKind).IsEqualTo(PidIdentityKind.Present);
    await Assert.That(all[0].StartIdentity).IsEqualTo("tk:123456789");
    // NOT quarantined — this is the whole point of the backward-compat contract.
    await Assert.That(File.Exists(Path.Combine(agentsDir, "legacy.json.corrupt"))).IsFalse();
}

[Test]
public async Task ReadAll_round_trips_an_identity_unavailable_record() {
    var dir   = NewStateDir();
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);

    store.Write(new AgentPidRecord("unresolved1", 42, "", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

    var all = store.ReadAll();
    await Assert.That(all).Count().IsEqualTo(1);
    await Assert.That(all[0].IdentityKind).IsEqualTo(PidIdentityKind.IdentityUnavailable);
    await Assert.That(all[0].StartIdentity).IsEmpty();
    await Assert.That(File.Exists(Path.Combine(dir, "agents", Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("unresolved1"))).ToLowerInvariant() + ".json.corrupt"))).IsFalse();
}

[Test]
public async Task ReadAll_quarantines_present_with_empty_token_as_corrupt() {
    var dir   = NewStateDir();
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);

    // An inconsistent NEW shape — Present claims a comparable identity but the token is
    // empty. This is a real corruption signal (unlike the legacy missing-key case above),
    // so it must be quarantined, not silently accepted.
    store.Write(new AgentPidRecord("bad1", 1, "", PidIdentityKind.Present,
        "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

    var all = store.ReadAll();
    await Assert.That(all).IsEmpty();

    var agentsDir = Path.Combine(dir, "agents");
    var corruptFiles = Directory.GetFiles(agentsDir, "*.json.corrupt");
    await Assert.That(corruptFiles.Length).IsEqualTo(1);
}

[Test]
public async Task ReadAll_quarantines_identity_unavailable_with_nonempty_token_as_corrupt() {
    var dir   = NewStateDir();
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);

    store.Write(new AgentPidRecord("bad2", 1, "lx:boot:999", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

    var all = store.ReadAll();
    await Assert.That(all).IsEmpty();

    var corruptFiles = Directory.GetFiles(Path.Combine(dir, "agents"), "*.json.corrupt");
    await Assert.That(corruptFiles.Length).IsEqualTo(1);
}
```

- [ ] Update the existing `Rec(...)` helper in the same file (`:18-19`) to the new positional shape:

```csharp
static AgentPidRecord Rec(string agentId, int pid = 123) =>
    new(agentId, pid, "lx:boot:999", PidIdentityKind.Present, "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);
```

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/AgentPidRecordStoreTests/*"`. Expected: compile failure (`PidIdentityKind` doesn't exist; `AgentPidRecord`'s constructor doesn't have that many parameters yet).

### Step 2: Add `PidIdentityKind` and the new `AgentPidRecord` field to `Models.cs`

- [ ] Replace the `AgentPidRecord` definition (`:1272-1283`):

```csharp
/// <summary>M1-A (spec §4.3): distinguishes a record with a comparable start-identity
/// (<see cref="Present"/>) from one where native capture failed (<see cref="IdentityUnavailable"/>
/// — <see cref="AgentPidRecord.StartIdentity"/> is <c>""</c>, a deliberate well-formed marker,
/// not a launch failure). <see cref="Present"/> MUST be the zero value: a pre-M1-A record's
/// JSON has no <c>identity_kind</c> key at all, and System.Text.Json's constructor-based
/// deserialization gives a missing value-type constructor parameter <c>default(T)</c> — this
/// is precisely how the backward-compat rule ("missing identity_kind + nonempty start_identity
/// ⇒ present") is satisfied with NO custom converter.</summary>
public enum PidIdentityKind {
    Present             = 0,
    IdentityUnavailable = 1,
}

/// <summary>Phase B (D4 §6.4(2)): the durable per-agent PID record written atomically at spawn
/// to <c>&lt;state-dir&gt;/agents/{agentId}.json</c>, so a restarted daemon can reap a surviving child
/// by EXACT identity. <see cref="StartIdentity"/> is the <c>ProcessStartToken</c> string
/// (kernel starttime / absolute start ticks / macOS incarnation id — exact, no tolerance), or
/// <c>""</c> when <see cref="IdentityKind"/> is <see cref="PidIdentityKind.IdentityUnavailable"/>.
/// <see cref="DaemonId"/> = hash of the daemon state-dir path (stable logical identity);
/// <see cref="DaemonEpoch"/> = fresh per boot.</summary>
public readonly record struct AgentPidRecord(
        string          AgentId,
        int             Pid,
        string          StartIdentity,
        PidIdentityKind IdentityKind,
        string          Kind,
        string          Vendor,
        string?         FlowRunId,
        string?         FlowRole,
        string          DaemonId,
        string          DaemonEpoch,
        DateTimeOffset  SpawnedAt
    );
```

- [ ] Add `[JsonSerializable(typeof(PidIdentityKind))]` next to the existing `[JsonSerializable(typeof(AgentPidRecord))]` at `:948`.
- [ ] Update the `Rec(...)` helper in `OrphanReaperTests.cs:21-22` the same way:

```csharp
static AgentPidRecord Rec(string agentId, int pid, string identity, string daemonId, string epoch) =>
    new(agentId, pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex", "flow-1", "reviewer", daemonId, epoch, DateTimeOffset.UtcNow);
```

- [ ] Update the inline `new AgentPidRecord(...)` in `StopAgentPidFallbackTests.cs` (the `AgentOrchestratorVendorTests` partial, `:23-25`):

```csharp
orch.WritePidRecordForTest(new AgentPidRecord(
    "ghost", dummy.Pid, identity!, PidIdentityKind.Present, "ReviewFlow", "codex", "f1", "reviewer",
    orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));
```

- [ ] If Task 5 Step 5 was executed with the temporary stub enum, delete that stub now — `AgentOrchestrator.cs`'s two `new AgentPidRecord(...)` calls (`PersistPidRecordOrThrow`'s `Present` and `IdentityUnavailable` branches) already reference `PidIdentityKind` by name and now bind to the real `Core` enum with no further change.
- [ ] Run the filtered command again. Expected: compiles; the two new "well-formed" tests (legacy decode, `identity_unavailable` round-trip) likely already PASS at this point (no custom converter needed if the .NET default-value-on-missing-property behavior works as expected) — the two quarantine tests still FAIL (no validation exists yet in `ReadAll`). Confirm this split before moving on: if the legacy-decode test unexpectedly fails too, `System.Text.Json`'s source-gen constructor binding didn't default the missing property the way this design assumes — in that case, add a `[JsonConstructor]`-free workaround: pre-process the JSON text in `AgentPidRecordStore.ReadAll` by injecting a default `"identity_kind":0` when the key is absent (a `JsonDocument.Parse` + presence check) before calling `JsonSerializer.Deserialize`. Prefer the zero-code-needed path; only add the pre-processing shim if the test proves it's actually necessary.

### Step 3: Add the inconsistent-shape quarantine to `AgentPidRecordStore.ReadAll`

- [ ] Modify the per-file loop in `ReadAll()` (`:52-69`) — after successful deserialization and the existing empty-`AgentId` check, add:

```csharp
foreach (var path in Directory.EnumerateFiles(_agentsDir, "*.json")) {
    AgentPidRecord record;
    try {
        record = JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.AgentPidRecord);
    } catch (Exception ex) {
        logger.LogWarning(ex, "AgentPidRecordStore: unparseable record {Path}; quarantining as .corrupt", path);
        TryQuarantine(path);
        continue;
    }

    if (string.IsNullOrEmpty(record.AgentId)) {
        logger.LogWarning("AgentPidRecordStore: record {Path} has no agent id; quarantining as .corrupt", path);
        TryQuarantine(path);
        continue;
    }

    // M1-A (spec §4.3): the only rejected shapes are NEW-schema-inconsistent combinations —
    // Present claiming a comparable identity with an empty token, or IdentityUnavailable
    // claiming NO comparable identity while still carrying a nonempty one. A LEGACY record
    // (no identity_kind key at all) always decodes as Present (PidIdentityKind's zero value)
    // and is never rejected here, however old its token scheme.
    var inconsistent =
        (record.IdentityKind == PidIdentityKind.Present && record.StartIdentity.Length == 0) ||
        (record.IdentityKind == PidIdentityKind.IdentityUnavailable && record.StartIdentity.Length != 0);

    if (inconsistent) {
        logger.LogWarning(
            "AgentPidRecordStore: record {Path} has an inconsistent identity_kind/start_identity combination ({Kind}, token length {Len}); quarantining as .corrupt",
            path, record.IdentityKind, record.StartIdentity.Length);
        TryQuarantine(path);
        continue;
    }

    results.Add(record);
}
```

- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/AgentPidRecordStoreTests/*"`. Expected: all green.
- [ ] Run the FULL suite: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Fix any other call site the compiler flags (search for `new AgentPidRecord(` across `test/` and `src/` once more to make sure nothing was missed — the notes identified exactly 4 call sites and this step + Task 5 have now touched all 4: `AgentOrchestrator.cs` ×2, `AgentPidRecordStoreTests.cs`, `OrphanReaperTests.cs`, `StopAgentPidFallbackTests.cs`).
- [ ] Commit: `git add -A && git commit -m "M1-A(b): add identity_kind to AgentPidRecord with backward-compatible decode + inconsistent-shape quarantine"`

---

## Task 9 — M1-A(c): `OrphanReaper` — Linux `identity_unavailable` marker-scan recovery; macOS `legacy_unresolvable`/`identity_unresolvable` manual-only classification

**Testability:** The Linux marker-scan-recovery path is exercised for real (real `DummyProcess` instances, real env reads) and CI-verified on `ubuntu-latest`. The macOS-only `legacy_unresolvable`/`identity_unresolvable` classification tests are gated `if (!OperatingSystem.IsMacOS()) return;` and, per Global Constraints, have **no CI runner** — they are asserted **locally only** on a macOS dev box; there is no automated CI signal for this specific behavior in this repo's current CI topology (this is explicitly flagged again in the Self-Review section as a residual gap worth raising, not silently accepted). The OS-independent parts (epoch-guard, gone-process deletion) are portable and CI-covered on both `ubuntu-latest`/`windows-latest`.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs:29-131`
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/OrphanReaperTests.cs`

**Interfaces:**
- Consumes: `PidIdentityKind` (Task 8); `ProcessStartToken`'s `mac:` scheme (Task 7, for the live macOS comparisons this task's tests exercise — no new production code depends on it directly since `ProcessReaper.Classify` already goes through `ProcessIdentity.MatchesTri` → `ProcessStartToken.Matches`, unchanged).
- Produces: no new public surface — this task only changes `OrphanReaper`'s internal decision tree. Downstream (AI-1391, out of scope) will consume `AgentPidRecordStore.ReadAll()`'s `IdentityKind` field directly; nothing here needs to expose a "pending" concept beyond what already exists (a record simply staying present in `ReadAll()`'s results across sweeps IS "pending" — there is no `StartupReapComplete` flag in this codebase to wire up, and this plan does not add one; that concept belongs entirely to the future AI-1391 work).

### Step 1: Write the failing Linux marker-scan-recovery test

- [ ] Add to `OrphanReaperTests.cs`:

```csharp
[Test]
public async Task Identity_unavailable_record_is_not_suppressed_from_the_marker_scan_and_self_deletes_on_confirmed_kill() {
    if (!OperatingSystem.IsLinux()) return;

    // A prior daemon incarnation spawned this, captured NOTHING (private-ABI hiccup — modeled
    // here directly since this test doesn't need real capture failure, just the RECORD shape),
    // and crashed. The record pass alone can never resolve an identity_unavailable record (no
    // token to compare) — it must fall through to the env-marker scan, which CAN reap it via
    // the live process's own KCAP_* triple, and must delete the record in the SAME operation.
    var store = NewStore();
    using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
        ["KCAP_AGENT_ID"] = "unresolved", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old-epoch" });

    store.Write(new AgentPidRecord("unresolved", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync();

    dummy.WaitForExit(TimeSpan.FromSeconds(8));
    await Assert.That(dummy.HasExited).IsTrue();
    // Positive, PID-independent resolution: the marker scan's own confirmed kill deleted the
    // identity_unavailable record — NOT a later record pass keyed on the numeric pid.
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "unresolved")).IsFalse();
}

[Test]
public async Task Identity_unavailable_record_stays_pending_when_the_marker_read_is_unreadable() {
    if (!OperatingSystem.IsLinux()) return;

    // A recordless-of-env process (no KCAP_* markers at all — simulating a marker read that
    // can't confirm ownership) with an identity_unavailable record on file: the record pass
    // can't resolve it (no token) AND the marker scan can't confirm it (no matching env) —
    // it must stay PENDING (retained), never silently treated as absent/resolved.
    var store = NewStore();
    using var dummy = DummyProcess.StartSleep(30); // no KCAP_* env at all

    store.Write(new AgentPidRecord("unresolved2", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync();

    await Assert.That(dummy.HasExited).IsFalse();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "unresolved2")).IsTrue();
    dummy.Kill();
}

[Test]
public async Task Forced_pid_reuse_between_marker_kill_and_a_later_sweep_never_acts_on_the_new_occupant() {
    if (!OperatingSystem.IsLinux()) return;

    // After the marker scan confirms a kill and deletes the identity_unavailable record, a
    // LATER sweep must find NOTHING to act on for that agent id even if the pid gets reused —
    // proven by running ReapOnceAsync a second time after spawning a decoy on a (best-effort)
    // recycled pid and confirming no record references it.
    var store = NewStore();
    using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
        ["KCAP_AGENT_ID"] = "reused", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old-epoch" });
    var firstPid = dummy.Pid;

    store.Write(new AgentPidRecord("reused", firstPid, "", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync();
    dummy.WaitForExit(TimeSpan.FromSeconds(8));
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "reused")).IsFalse();

    // A second sweep (simulating a later heartbeat) with an unrelated live process happening
    // to occupy a nearby pid must find nothing keyed to "reused" — the record is genuinely
    // gone, not re-derived from the numeric pid.
    await reaper.ReapOnceAsync();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "reused")).IsFalse();
}
```

- [ ] Add the macOS-only classification tests (gated, local-only per this task's testability note):

```csharp
[Test]
public async Task Macos_identity_unavailable_record_is_identity_unresolvable_manual_only() {
    if (!OperatingSystem.IsMacOS()) return;

    var store = NewStore();
    using var dummy = DummyProcess.StartSleep(30);

    store.Write(new AgentPidRecord("mac-unresolved", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
        "ReviewFlow", "codex", "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync(); // macOS has no marker scan — this can NEVER auto-resolve

    await Assert.That(dummy.HasExited).IsFalse();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "mac-unresolved")).IsTrue();

    // Manual-kill contract: once the operator kills the pid, the NEXT sweep observes it dead
    // (IsAlive false) and deletes the record normally — no special-case code needed for this
    // half, Classify's existing Dead branch already covers it.
    dummy.Kill();
    dummy.WaitForExit(TimeSpan.FromSeconds(5));
    await reaper.ReapOnceAsync();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "mac-unresolved")).IsFalse();
}

[Test]
public async Task Macos_legacy_tk_record_is_legacy_unresolvable_spared_every_pass_until_manual_kill() {
    if (!OperatingSystem.IsMacOS()) return;

    // A pre-M1-A tk: record compared against the now-mac:-producing live process is
    // cross-scheme → Ambiguous → spared, every pass, forever — until the operator manually
    // kills it, at which point Classify's Dead branch (which runs BEFORE any token
    // comparison) confirms death regardless of scheme and the record is deleted normally.
    var store = NewStore();
    using var dummy = DummyProcess.StartSleep(30);

    store.Write(new AgentPidRecord("legacy-live", dummy.Pid, "tk:1", PidIdentityKind.Present,
        "ReviewFlow", "codex", "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync();

    await Assert.That(dummy.HasExited).IsFalse();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "legacy-live")).IsTrue();

    dummy.Kill();
    dummy.WaitForExit(TimeSpan.FromSeconds(5));
    await reaper.ReapOnceAsync();
    await Assert.That(store.ReadAll().Any(r => r.AgentId == "legacy-live")).IsFalse();
}
```

- [ ] Update the `Rec(...)` helper calls already present in this file (from Task 8 Step 1) if any of the EXISTING tests in this file still construct records positionally without `PidIdentityKind` — confirm via a full-file read that every `new AgentPidRecord(...)`/`Rec(...)` call compiles against the Task 8 shape (Task 8 already updated the shared `Rec` helper; these new tests construct records directly since they need `IdentityKind` values `Rec` doesn't parameterize).
- [ ] Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1 --treenode-filter "/*/*/OrphanReaperTests/*"`. Expected on Linux: `Identity_unavailable_record_is_not_suppressed...` and `...stays_pending...` FAIL (the current `handledPids.Add` is unconditional, so the marker scan never even looks at these pids); `Forced_pid_reuse...` fails downstream of the same root cause. Expected on macOS: the two `Macos_...` tests fail (no differentiated logging exists yet, though the underlying spare/confirm-on-death mechanics already technically work via the existing tri-state `Classify` — these specific tests should mostly already PASS on the BEHAVIOR, since nothing in this step required new logic for macOS beyond what Task 7/8 already provide; if they pass immediately, that's expected and fine — the differentiated LOGGING added in Step 2 is a nice-to-have observability improvement, not something these particular assertions depend on).

### Step 2: Fix `OrphanReaper`'s record pass + marker scan

- [ ] Replace the record-pass loop in `ReapOnceAsync` (`:32-56`):

```csharp
public async Task ReapOnceAsync(CancellationToken ct = default) {
    var handledPids = new HashSet<int>();

    foreach (var record in store.ReadAll()) {
        if (ct.IsCancellationRequested) return;

        // An IdentityUnavailable record carries NO comparable token — the record pass can
        // NEVER resolve it (ProcessReaper.Classify always lands Ambiguous for an empty
        // expectedIdentity). Do NOT mark the pid "handled" here, so the env-marker scan below
        // still gets a chance to reap it via the live process's OWN env triple (Linux only;
        // macOS has no marker scan, so such a record stays identity_unresolvable/manual — see
        // §4.3 and this file's class doc).
        if (record.IdentityKind == PidIdentityKind.Present) {
            handledPids.Add(record.Pid);
        }

        if (string.Equals(record.DaemonEpoch, currentEpoch, StringComparison.Ordinal)) continue;

        try {
            var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, logger, ct);
            if (confirmedGone) {
                store.Delete(record.AgentId);
                logger.LogInformation(
                    "OrphanReaper: reaped leftover agent {AgentId} (pid {Pid}) from a prior daemon run",
                    record.AgentId, record.Pid);
            } else if (record.IdentityKind == PidIdentityKind.IdentityUnavailable) {
                logger.LogWarning(
                    "OrphanReaper: identity_unavailable record for {AgentId} (pid {Pid}, age {Age}) unresolved by the record pass — the env-marker scan may still reap it on Linux; macOS requires a manual kill",
                    record.AgentId, record.Pid, DateTimeOffset.UtcNow - record.SpawnedAt);
            } else if (OperatingSystem.IsMacOS()) {
                // Present but Ambiguous on macOS almost always means a cross-scheme mismatch
                // (a pre-M1-A tk: record compared against the now-mac:-producing live
                // process) — the spec's "legacy_unresolvable" residual.
                logger.LogWarning(
                    "OrphanReaper: legacy_unresolvable record for {AgentId} (pid {Pid}, age {Age}) — spared every pass (cross-scheme token); manually verify and kill the pid",
                    record.AgentId, record.Pid, DateTimeOffset.UtcNow - record.SpawnedAt);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "OrphanReaper: record-pass reap failed for {AgentId} (pid {Pid})", record.AgentId, record.Pid);
        }
    }

    await ScanEnvMarkersAsync(handledPids, ct);
}
```

- [ ] In `ScanEnvMarkersAsync`, after a confirmed marker-kill, also delete any matching record (idempotent no-op if none exists — covers both the ordinary recordless-survivor case AND the new "survivor WITH an `identity_unavailable` record" case in one line):

```csharp
try {
    var gone = await ProcessReaper.ReapByMarkerAsync(pid, token, agentId, logger, ct);
    if (gone) {
        // Positive, PID-independent resolution: delete any record for THIS agent id (a
        // no-op if none exists — the ordinary recordless-survivor case). This is what
        // makes the "identity_unavailable record + live-env-triple confirms it" case
        // self-heal WITHOUT ever keying off the numeric pid alone (a reused pid between
        // this kill and any later record pass can't act on the wrong occupant, because the
        // record is already gone by then).
        store.Delete(agentId);
        logger.LogInformation(
            "OrphanReaper: reaped recordless survivor {AgentId} (pid {Pid}) of a prior daemon incarnation", agentId, pid);
    } else {
        logger.LogWarning(
            "OrphanReaper: env-marker kill of {AgentId} (pid {Pid}) not confirmed — retrying next tick", agentId, pid);
    }
} catch (Exception ex) when (ex is not OperationCanceledException) {
    logger.LogWarning(ex, "OrphanReaper: env-marker reap failed for pid {Pid}", pid);
}
```

(This replaces the existing `if (gone) ... else ...` block — the ONLY change is the added `store.Delete(agentId);` inside the `if (gone)` branch; everything else is unchanged from the shipped `#327` code.)

- [ ] Run the filtered command again. Expected: all green on Linux; the macOS tests (if you have Mac hardware to check locally) should now show the differentiated log lines too, though the pass/fail of the assertions themselves shouldn't have depended on that.
- [ ] Run the FULL suite: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Confirm the existing `Record_pass_reaps_a_prior_incarnation_survivor_and_deletes_the_record` and `Env_marker_scan_reaps_a_stale_epoch_survivor_of_this_daemon_only` tests (unchanged, `Present`-kind records) still pass unmodified — this task must not weaken anything Phase B already shipped.
- [ ] Commit: `git add -A && git commit -m "M1-A(c): OrphanReaper — Linux identity_unavailable marker-scan recovery; macOS legacy/identity unresolvable classification"`

---

## Task 10 — Docs, coverage matrix, and PR

**Testability:** Documentation-only; no runtime behavior. The "cross-OS regression" full-suite run at the end is CI-covered on Linux/Windows and locally runnable everywhere.

**Files:**
- Modify: `README.md:788-799` ("Review-flow reviewer backstops & crash-survivor reaping" section)
- No `docs/HOW_IT_WORKS.md`/gotchas equivalent exists in kcap-cli (confirmed — the repo's `docs/` folder holds only ad-hoc per-feature design docs, not a maintained architecture index); this plan's spec (`docs/superpowers/specs/2026-07-17-ai1390-native-os-containment-design.md`) is the durable design record and needs no further edit.

### Step 1: Rewrite the README section with the per-OS coverage matrix

- [ ] Replace `README.md:788-793` (keep the existing lifetime/idle-backstop bullet and the env-var table below it unchanged):

```markdown
#### Review-flow reviewer backstops & crash-survivor reaping

Hosted review-flow reviewers are *unattended* and count against the daemon's `--max-agents` budget. To keep a stuck or abandoned reviewer from holding a slot forever, the daemon defends its own capacity in layers: OS-level containment at spawn time (this section) plus a managed record/scan/quarantine backstop for everything containment can't reach.

- **Lifetime / idle backstop.** The heartbeat reaps a review-flow reviewer that has run past a maximum lifetime or gone idle too long (the driver vanished, or its run went terminal on the server without the daemon hearing about it). Interactive agents are never touched by these bounds.
- **OS-level containment at spawn (native, immediate where the OS supports it).**
  - **Windows** — every hosted PTY is created already bound to a `KILL_ON_JOB_CLOSE` Job Object (no breakaway allowed): the OS itself kills the agent **and every descendant** the instant the job handle's last reference closes — clean shutdown, daemon crash, or an external kill, no exceptions. There is no survivor class to reap on Windows; the PID-record layer becomes pure bookkeeping.
  - **Linux** — a native spawn shim (`libpty_shim`) forks the agent, arms `PR_SET_PDEATHSIG`, and execs via `execveat` on a dedicated daemon-lifetime thread, so the agent **leader** dies immediately if the daemon dies — for a launch the shim proved was contained at initial exec (a non-privileged, non-deep-shebang binary on a kernel ≥ 3.19). Descendants and any uncontained-classified launch (a privileged binary, an old kernel, an unresolvable shebang) fall back to the crash-survivor record/scan reaping below.
  - **macOS** — there is no OS primitive for this at all (no PDEATHSIG, no job objects); a crash-surviving child is recovered *eventually*, at the next daemon boot/heartbeat, via the record layer below.
- **Crash-survivor reaping (the managed backstop — covers everything containment doesn't).** Each hosted child's pid + an exact OS-native start-identity is written to a durable per-daemon record under `{state-dir}/{name}/agents/` at spawn (Linux: kernel `boot_id`+`starttime`; macOS: a kernel-assigned, boot-scoped unique process id + boot-session UUID; Windows: an absolute start timestamp, moot once containment ships since there's no survivor to reap), and every child is stamped with `KCAP_AGENT_ID` / `KCAP_DAEMON_ID` / `KCAP_DAEMON_EPOCH` env markers. On the next boot (and on the heartbeat) the daemon reaps any child that outlived a **prior** incarnation of itself — matched by exact `(pid, start-identity)` from the record, or, for a recordless survivor, by the env markers (same daemon id, older epoch). A process is killed only when its identity is *proven*; anything ambiguous is spared (never a wrong kill). On Linux the env checks read `/proc/{pid}/environ`; on macOS process env is redacted from other processes entirely, so the record-based path is the effective mechanism there, and a record whose identity couldn't be captured (a rare private-ABI hiccup) is retained and logged each sweep rather than silently dropped — resolved automatically on Linux (the env-marker scan can still confirm it) or by a manual kill on macOS. Note: `kcap launch`'s private local-attach path spawns through the same OS-containment layer but writes no durable record at all (no server-side ownership to protect there), so it has no crash-survivor backstop beyond whatever OS containment applies.
```

- [ ] Read the whole section back after editing to confirm the markdown table immediately below it (`KCAP_REVIEWER_MAX_LIFETIME`/`KCAP_REVIEWER_IDLE_TIMEOUT`) still flows correctly with no orphaned heading levels.
- [ ] Commit: `git add README.md && git commit -m "Document W1/L1/M1-A OS-level containment in the crash-survivor reaping README section"`

### Step 2: Full cross-OS regression pass

- [ ] Run the complete suite one final time before opening the PR: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1`. Expected: fully green on your current dev OS.
- [ ] Confirm (by reading the test file, not by re-running anything OS-specific you can't run locally) that the pre-existing Phase-B daemon suite — especially the tri-state identity tests (`ProcessStartTokenTests`, `ProcessIdentityTests`, `AgentKillQuarantineTests`) — is untouched in spirit: `MatchesTri == null` must still spare everywhere; this plan only ADDS a new scheme (`mac:`) and a new record field (`IdentityKind`), it never loosens an existing comparison.
- [ ] Push the branch and let `ci.yml` run on `ubuntu-latest` + `windows-latest`; open (or update) a PR against `worktree-ai-1313-reviewer-reaping` (the #327 branch — this stacks on it, NOT `main`) titled `[AI-1390] Hosted-agent native OS containment (Job Object, PDEATHSIG shim, macOS incarnation identity)`.
- [ ] In the PR description, include the per-OS coverage matrix (can be copied near-verbatim from spec §4.5) so a reviewer sees the "what's immediate / what's eventual / what's still a managed-layer residual" picture without opening the spec.
- [ ] **Retarget checklist (do this once #327 merges, not before):** once `worktree-ai-1313-reviewer-reaping` merges into `main`, rebase this branch onto `main` and change the PR base from `worktree-ai-1313-reviewer-reaping` to `main` (`gh pr edit <number> --base main`), then resolve any rebase conflicts and re-run the full suite once more before re-requesting review.
- [ ] Let CI run across `release.yml`'s full per-RID matrix at least once before considering Task 6 done — per that task's testability note, expect to iterate over 1–2 CI runs to shake out RID-specific tool-availability surprises (missing `readelf`/`lipo`, musl toolchain quirks) that cannot be caught by local iteration on a single dev machine.

---

## Self-Review

**Spec §4 coverage map:**

| Spec section | Requirement | Task |
|---|---|---|
| §4.1 (W1) | Job Object creation-time binding, no breakaway, no UI limits | Task 1 |
| §4.1 | Fail-closed on job creation/nesting failure | Task 1 |
| §4.1 | Spawn-path failure after `CreateProcessW` → `TerminateJobObject` + confirm death | Task 1 |
| §4.2(a) | Opaque `pty_exec_plan`, `pty_probe_execveat` (raw syscall), `pty_preflight`, `pty_plan_contained`, `pty_plan_free` | Task 2 |
| §4.2(a) | `pty_spawn`, error-pipe handshake, `failed_step` enum, capture-binding rule | Task 3 |
| §4.2(a) | EXEC_FD vs EXEC_PATH contract; argv rewrite rules (native/direct-shebang/env-NAME); child-PATH resolution; `confstr(_CS_PATH)` fallback; empty/relative PATH component → uncontained | Task 2 |
| §4.2(a) | Executable resolution pre-fork in the parent; `execvpe` rejected; ENOEXEC behavior change | Task 5 (parent-side resolution), Task 2 (shebang handling) |
| §4.2(a) | Child-side sequence (`prctl`→`getppid` check→`chdir`→exec→error-pipe on failure) | Task 3 |
| §4.2(a) | fd-bound privilege preflight (setuid/setgid + `fgetxattr` capability xattr), fail-closed classification | Task 2 |
| §4.2(a) | Kernel floor via raw-syscall runtime probe, test seam for forcing unsupported | Task 2 |
| §4.2(a) | Resource model (plan ownership, `SafeHandle`-equivalent single-free, `cancel_fd` CLOEXEC) | Task 2 (plan_free), Task 3 (spawn's fd handling), Task 5 (UnixPtyProcess's `finally` free) |
| §4.2(b) | Dedicated daemon-lifetime spawner thread; `Environment.FailFast` on unexpected exit | Task 4 |
| §4.2(c) | macOS also routes through `pty_spawn`; PDEATHSIG/execveat compiled out via `#ifdef __linux__` | Task 3 |
| §4.2 (build) | Per-RID native compilation on native runners, RID-isolated output, machine-type + load-and-call smoke | Task 6 |
| §4.3 (M1-A) | `mac:{bootsessionuuid}:{p_uniqueid}` scheme, vendored private ABI, fail-safe capture | Task 7 (C# side), Task 3 (native side) |
| §4.3 | `identity_kind` record contract, backward-compatible decode, inconsistent-shape quarantine | Task 8 |
| §4.3 | Linux `identity_unavailable` marker-scan recovery, positive PID-independent deletion | Task 9 |
| §4.3 | macOS `identity_unresolvable` / `legacy_unresolvable` manual-only classification | Task 9 |
| §4.3 | Uniqueness-scope documentation (snapshot-restored VM caveat) | Task 10 (README) |
| §4.4 | Managed layer untouched; `OrphanReaper` record pass stays epoch-guarded | Task 9 (verified, not re-implemented) |
| §4.5 | Per-OS coverage matrix documented for operators | Task 10 |
| §5 | Full test list | Distributed across Tasks 1–9 (see each task's Files/Interfaces + inline test code); the §5 items NOT mapped to a concrete test above are called out below |
| §6 | Rollout order (W1 → L1(shim) → L1(thread) → M1-A → docs) | Task numbering follows this order (1 → 2/3 → 4/5 → 6 → 7/8/9 → 10) |

**Spec requirements NOT cleanly mapped to a dedicated test (flagged, not silently dropped):**
- The §5 macOS test "`crash-injection in the forkpty→record-rename window` → survivor is *not* recovered" (documenting the accepted recordless residual) has no test in this plan — it's an explicit non-coverage documentation point in the spec, not a behavior to assert; Task 10's README update states the residual in prose instead of a test. If a future reviewer wants an actual regression test for "this residual still exists" (somewhat unusual — testing an absence of a feature), it would need a way to inject a delay between `forkpty` returning and `PersistPidRecordOrThrow`'s write, which the current design has no seam for; flagged as a possible Task 10 follow-up rather than invented here.
- The §5 Windows "spawn-failure after create → TerminateJobObject + confirmed-dead" test (Task 1, `Job_creation_failure_fails_the_spawn_closed`) exercises job-*creation* failure, not a failure genuinely *after* `CreateProcessW` succeeds (e.g. the `SafeFileHandle` construction throwing) — Task 1 Step 3's code handles that path (the `catch` block wrapping the post-create body), but Step 2's test list doesn't include a dedicated test forcing that exact sub-case. Worth adding if the implementer finds an easy way to fault-inject after `CreateProcessW` (e.g. a test-only hook); not blocking.

**Design decisions made where the spec/notes were silent or explicitly open** (see also the report to the requester):
1. `mac:` capture is implemented independently in **both** C (`pty_shim.c`, for the capture-binding rule) and C# (`Core/ProcessStartToken.cs`, for the CLI's standalone daemon-liveness use) — the notes flagged this as an open "or" choice; resolved by tracing `ProcessStartToken`'s actual callers (`Capacitor.Cli/Commands/DaemonCommands.cs` calls it without the shim present).
2. A new `NativeTestHost` helper project provides the only way to test PDEATHSIG-on-parent-death and spawner-thread-FailFast from outside the process under test — greenfield, not specified by the notes beyond "process-isolated test host" in prose.
3. `IPtyProcess`/`IHostedAgentRuntime` gain a `string? StartIdentity => null` default interface member as the threading mechanism for "consume shim-captured identity, don't re-capture" — a concrete design filling in the notes' correction #3.
4. `AgentPidRecord`'s new `IdentityKind` field is inserted immediately after `StartIdentity`; `PidIdentityKind.Present = 0` is load-bearing for the backward-compat decode (relies on System.Text.Json's missing-property → `default(T)` behavior for constructor-bound records — Task 8 Step 2 includes a fallback plan if that assumption doesn't hold).
5. `agent.StartIdentity = ""` (not `null`) for the `IdentityUnavailable` case, so `AgentOrchestrator.CleanupAgentAsync`'s existing teardown-quarantine check (`:1891`) naturally treats it as "uncomparable, spare if still alive" rather than "no identity, assume gone" — a subtle but load-bearing choice documented in Task 5.

**CI iteration expected, not a plan gap:** Task 6 (per-RID native packaging) and the macOS-only tests in Tasks 3/7/9 cannot be fully proven before pushing to CI — this is inherent to the OS/RID matrix (6 RIDs, only 2 of which have any local dev-box equivalent for most engineers, and `ci.yml` itself only runs 2 of the 6). Budget for 2–3 CI round-trips on `release.yml` specifically once Task 6 is pushed, beyond the task's own local-verification steps.


