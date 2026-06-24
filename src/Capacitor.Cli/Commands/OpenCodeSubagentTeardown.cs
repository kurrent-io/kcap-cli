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

            // Each step best-effort + independent so subagent-stop (→ SubagentCompleted) is
            // always attempted even if the kill or drain hiccups.
            await SafeAsync(() => WatcherManager.KillWatcher($"{sessionId}-{agentId}"));
            await SafeAsync(() => WatcherManager.InlineDrainAsync(baseUrl, sessionId, subFile, agentId, vendor: "opencode"));
            await SafeAsync(() => PostStopAsync(baseUrl, sessionId, agentId, agentType, subFile));
        }
    }

    static async Task PostStopAsync(string baseUrl, string sessionId, string agentId, string agentType, string subFile) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       payload = OpenCodeSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", content);
    }

    static async Task SafeAsync(Func<Task> op) {
        try { await op(); } catch { /* best effort — kcap import recovers anything missed */ }
    }
}
