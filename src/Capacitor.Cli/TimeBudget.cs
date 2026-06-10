namespace Capacitor.Cli;

public static class TimeBudget {
    /// <summary>
    /// Runs <paramref name="work"/> but returns after at most <paramref name="cap"/>.
    /// Returns <c>true</c> when the work finished within the cap (its exception, if any, is
    /// propagated), or <c>false</c> when the cap elapsed first and the work was abandoned
    /// (left running, its later result/fault unobserved). The boolean lets callers log that
    /// the work was cut short.
    ///
    /// Used to keep best-effort hook pre-work — killing the watcher and inline-draining the
    /// transcript — from consuming the entire SessionEnd hook timeout. A slow or retrying
    /// remote call here could otherwise burn the whole 15s budget (the retry helper alone
    /// allows up to 30s) and starve the session-end POST, leaving the session stuck "Active"
    /// forever. The server's own StopAndDrain plus the "kcap import" recovery hint cover
    /// anything an abandoned drain didn't finish.
    /// </summary>
    public static async Task<bool> RunCappedAsync(Func<Task> work, TimeSpan cap) {
        var workTask = work();

        using var cts       = new CancellationTokenSource();
        var       delayTask = Task.Delay(cap, cts.Token);

        await Task.WhenAny(workTask, delayTask);
        await cts.CancelAsync(); // stop the timer either way

        // Observe the work whenever it has actually finished — even if the delay "won" a
        // near-boundary race — so a completed task's result/exception is never silently
        // dropped. Only abandon when the work is genuinely still running past the cap.
        if (workTask.IsCompleted) {
            await workTask; // propagate exception if faulted
            return true;
        }

        // Cap elapsed: abandon the work. Swallow any later fault so it doesn't surface as an
        // UnobservedTaskException — the hook process exits shortly after the POST anyway.
        _ = workTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        return false;
    }
}
