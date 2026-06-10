namespace Capacitor.Cli;

public static class TimeBudget {
    /// <summary>
    /// Runs <paramref name="work"/> but returns after at most <paramref name="cap"/>.
    /// If the cap elapses first, the work is abandoned (left running, its result/fault
    /// unobserved) and control returns to the caller. Exceptions are propagated only when
    /// the work completes within the cap.
    ///
    /// Used to keep best-effort hook pre-work — killing the watcher and inline-draining the
    /// transcript — from consuming the entire SessionEnd hook timeout. A slow or retrying
    /// remote call here could otherwise burn the whole 15s budget (the retry helper alone
    /// allows up to 30s) and starve the session-end POST, leaving the session stuck "Active"
    /// forever. The server's own StopAndDrain plus the "kcap import" recovery hint cover
    /// anything an abandoned drain didn't finish.
    /// </summary>
    public static async Task RunCappedAsync(Func<Task> work, TimeSpan cap) {
        var workTask = work();

        using var cts       = new CancellationTokenSource();
        var       delayTask = Task.Delay(cap, cts.Token);

        if (await Task.WhenAny(workTask, delayTask) == workTask) {
            await cts.CancelAsync(); // stop the timer
            await workTask;          // observe result / propagate exception
            return;
        }

        // Cap elapsed: abandon the work. Swallow any later fault so it doesn't surface as an
        // UnobservedTaskException — the hook process exits shortly after the POST anyway.
        _ = workTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }
}
