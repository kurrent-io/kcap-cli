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
    volatile bool                             _testCrashRequested;

    public UnixSpawnerThread() {
        _thread = new Thread(RunLoop) { IsBackground = false, Name = "kcap-unix-spawner" };
        _thread.Start();
    }

    /// <summary>Submit a spawn request to the dedicated thread and block until it completes.
    /// Throws <see cref="InvalidOperationException"/> if called after <see cref="Dispose"/> —
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
                if (_testCrashRequested) {
                    // Test-only seam (see CrashForTest below): deliberately escape BEFORE the
                    // per-request try/catch below, so this behaves exactly like a genuinely
                    // unexpected bug in the loop itself — not an ordinary pty_spawn failure,
                    // which stays scoped to its own request via SetException.
                    throw new InvalidOperationException("UnixSpawnerThread test-forced crash");
                }

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
        _queue.Dispose();
    }

    /// <summary>Test-only seam (the "unexpected spawner-thread termination fail-fasts" case):
    /// forces the loop to throw from a genuinely unexpected place — outside the per-request
    /// try/catch — so the <see cref="Environment.FailFast"/> path fires exactly like a real
    /// native-call bug would. Pushes one dummy request to wake the (blocked)
    /// <see cref="BlockingCollection{T}.GetConsumingEnumerable"/> loop; the next iteration
    /// observes <see cref="_testCrashRequested"/> and throws before entering the normal
    /// per-request handling.</summary>
    internal void CrashForTest() {
        _testCrashRequested = true;
        _queue.Add(new SpawnRequest(IntPtr.Zero, [], "", 0, 0, -999999 /* unused */, -1));
    }

    // Nested (rather than a top-level `file`-scoped type): a file-local type cannot appear in
    // ANY member signature — including a private field's type — of a type that is itself not
    // file-scoped (CS9051), and UnixSpawnerThread must stay `internal` (consumed by Task 5 and
    // by tests via InternalsVisibleTo). Nesting keeps this an implementation detail without
    // hitting that restriction.
    sealed record SpawnRequest(
        IntPtr Plan, string?[] Envp, string Cwd, ushort Rows, ushort Cols, int ExpectedParent, int CancelFd) {
        public TaskCompletionSource<UnixPtyInterop.PtySpawnResult> Completion { get; } = new();
    }
}
