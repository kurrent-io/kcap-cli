using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Bounds a hook's client creation, the one step that can wait on the cross-process refresh lock
/// for the whole WorkOS replay budget. Every host kills a hook long before that, so a hook that
/// cannot authenticate inside its cap takes its spool path instead of being killed on the way there.
/// </summary>
internal static class BoundedAuth {
    /// <summary>
    /// The client and its auth outcome if created within <paramref name="cap"/>; null when the cap
    /// elapsed first. The abandoned creation is observed to completion so a late fault never surfaces
    /// as an unobserved exception, and a client it eventually produced is disposed.
    /// <paramref name="onAbandoned"/> runs whenever creation is given up on: the process may exit
    /// with a rotation in flight, so the caller hands the refresh to something that will outlive it.
    /// </summary>
    internal static async Task<AuthAttempt?> CreateClientWithinAsync(
            Func<Task<AuthAttempt>> factory, TimeSpan cap, Action? onAbandoned = null) {
        if (cap <= TimeSpan.Zero) {
            onAbandoned?.Invoke();

            return null;
        }

        var task   = factory();
        var winner = await Task.WhenAny(task, Task.Delay(cap));

        if (winner != task) {
            onAbandoned?.Invoke();

            _ = task.ContinueWith(static t => {
                if (t.IsFaulted) _ = t.Exception;
                else if (t.Status == TaskStatus.RanToCompletion) t.Result.Client.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return null;
        }

        try { return await task; } catch { return null; }
    }
}
