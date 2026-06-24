using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Shared session-end teardown for live OpenCode subagents (AI-919 phase 2). OpenCode
/// fires no subagent-stop hook and the child watchers the parent spawns carry no
/// parent-pid watchdog, so the parent session-end is the ONLY thing that finalizes them:
/// for each nested child transcript the plugin wrote, kill its child watcher (no-op if
/// already gone), drain its tail (resumes from the server watermark), and POST
/// <c>/hooks/subagent-stop</c> so the server writes <c>SubagentCompleted</c> + the agent
/// summary. Mirrors <see cref="GeminiSubagentTeardown"/>.
///
/// Enumerates the on-disk files rather than an in-memory set, so it recovers subagents
/// even after a parent-watcher restart/crash. Invoked from the watcher's parent-exit
/// fallback (<see cref="WatchCommand.PostSessionEndOnParentExitAsync"/>) — the only
/// session-end path OpenCode has. Best-effort per step (a failure on one subagent — or
/// one step — never skips the rest).
/// </summary>
static class OpenCodeSubagentTeardown {
    /// <summary>Time budget on the shutdown path so a slow drain can't block termination.</summary>
    internal static readonly TimeSpan DrainCap = TimeSpan.FromSeconds(8);

    internal static async Task DrainAsync(string baseUrl, string sessionId, string parentTranscriptPath) {
        var subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(parentTranscriptPath);
        if (subFiles.Count == 0) return;

        foreach (var subFile in subFiles) {
            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            // Each step is independently CAPPED so a slow first child can't consume the
            // budget and starve later children of their SubagentCompleted:
            //  - KillWatcher waits up to 5s for graceful exit (WatcherManager) — cap it hard;
            //  - InlineDrain overlaps harmlessly with any still-live watcher (the server
            //    dedupes by deterministic event id);
            //  - subagent-stop ALWAYS runs (the critical SubagentCompleted; the server
            //    also stop-and-drains the watcher).
            await CappedAsync(() => WatcherManager.KillWatcher($"{sessionId}-{agentId}"),                               TimeSpan.FromSeconds(1.5));
            await CappedAsync(() => WatcherManager.InlineDrainAsync(baseUrl, sessionId, subFile, agentId, vendor: "opencode"), TimeSpan.FromSeconds(2.5));
            await CappedAsync(() => PostStopAsync(baseUrl, sessionId, agentId, agentType, subFile),                     TimeSpan.FromSeconds(2.5));
        }
    }

    static async Task PostStopAsync(string baseUrl, string sessionId, string agentId, string agentType, string subFile) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       payload = OpenCodeSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", content);
    }

    /// <summary>
    /// Runs a best-effort teardown step bounded by <paramref name="cap"/>; swallows errors
    /// and timeouts so one subagent (or one slow step) never blocks the rest of the shutdown
    /// path. On timeout the step is detached and its fault observed (no unobserved-exception).
    /// </summary>
    static async Task CappedAsync(Func<Task> op, TimeSpan cap) {
        Task task;
        try { task = op(); } catch { return; }

        if (await Task.WhenAny(task, Task.Delay(cap)) != task) {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        try { await task; } catch { /* best effort — kcap import recovers anything missed */ }
    }
}
