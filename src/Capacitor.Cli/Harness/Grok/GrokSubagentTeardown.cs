using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Grok;

/// <summary>
/// Ends a Grok subagent the parent watcher registered: kills its child watcher, drains the rest of its
/// transcript, and posts <c>/hooks/subagent-stop</c>. Grok fires no subagent hook, and a child watcher
/// has no parent-pid watchdog, so this runs when the parent's transcript records the child finishing
/// and again for every child when the parent exits. The server keeps one stop per subagent, so a
/// second finish is harmless.
/// </summary>
sealed class GrokSubagentTeardown(ProfileContext profiles, ICapacitorHttpClient http, WatcherManager watchers, TimeProvider time) {
    /// <summary>The parent-exit budget, so a slow drain cannot hold up the watcher's own exit.</summary>
    internal static readonly TimeSpan DrainCap = TimeSpan.FromSeconds(8);

    internal async Task FinishAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType) {
        var agentId    = GrokSubagents.AgentId(childSessionId);
        var transcript = GrokSubagents.ChildTranscript(parentTranscript, childSessionId);

        // Independent steps, so the stop is attempted even when the kill or the drain fails.
        await SafeAsync(() => watchers.KillWatcher($"{sessionId}-{agentId}"));
        await SafeAsync(() => watchers.InlineDrainAsync(sessionId, transcript, agentId, vendor: "grok"));
        await SafeAsync(() => PostStopAsync(sessionId, agentId, agentType ?? "subagent", transcript));
    }

    /// <summary>Every subagent the parent's transcript names, read from disk so a restarted parent
    /// watcher still finds them.</summary>
    internal async Task DrainAsync(string sessionId, string parentTranscript) {
        string[] lines;
        try { lines = File.ReadAllTextShared(parentTranscript).Split('\n'); } catch (IOException) { return; }

        foreach (var spawned in GrokSubagents.Read(lines).Where(e => !e.Finished).DistinctBy(e => e.ChildSessionId))
            await FinishAsync(sessionId, parentTranscript, spawned.ChildSessionId, spawned.SubagentType);
    }

    async Task PostStopAsync(string sessionId, string agentId, string agentType, string transcript) {
        using var client  = await http.ForBackgroundAsync();
        var       payload = GeminiSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, transcript);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{profiles.Resolution.ServerUrl}/hooks/subagent-stop", content, time);
    }

    static async Task SafeAsync(Func<Task> op) {
        try { await op(); } catch { /* best effort; kcap import --grok recovers anything missed */ }
    }
}
