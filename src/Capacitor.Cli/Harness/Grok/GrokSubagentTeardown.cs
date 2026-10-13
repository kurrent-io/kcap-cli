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

    /// <param name="register">Post <c>subagent-start</c> first, for a child the live scan never
    /// registered; without it the drain has no watermark and sends nothing. Lifecycle ids are
    /// deterministic, so a repeat start is harmless.</param>
    /// <returns>Whether the stop is settled: accepted, or refused for good. Otherwise the caller retries.</returns>
    internal async Task<bool> FinishAsync(string sessionId, string parentTranscript, string childSessionId, string? agentType, bool register = false) {
        var agentId    = GrokSubagents.AgentId(childSessionId);
        var transcript = GrokSubagents.ChildTranscript(parentTranscript, childSessionId);

        if (register && !await StartAsync(sessionId, agentId, agentType ?? "subagent", transcript)) return false;

        // Independent steps, so the stop is attempted even when the kill or the drain fails.
        await SafeAsync(() => watchers.KillWatcher($"{sessionId}-{agentId}"));
        await SafeAsync(() => watchers.InlineDrainAsync(sessionId, transcript, agentId, vendor: "grok"));

        int status;
        try { status = await PostStopAsync(sessionId, agentId, agentType ?? "subagent", transcript); } catch { return false; }

        return status is >= 200 and < 300 || !HookSpool.IsRetryable(status);
    }

    /// <summary>Every subagent the parent's transcript names, read from disk so a restarted parent
    /// watcher still finds them.</summary>
    internal async Task DrainAsync(string sessionId, string parentTranscript) {
        // Yield first, so the caller's time cap is running before a long transcript is read.
        await Task.Yield();

        string[] lines;
        try { lines = (await File.ReadAllTextSharedAsync(parentTranscript)).Split('\n'); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        // A child whose transcript is gone may still have been registered live, so it is stopped without
        // registering; one whose transcript exists is registered first in case the live scan never was.
        foreach (var spawned in GrokSubagents.Read(lines).Where(e => !e.Finished).DistinctBy(e => e.ChildSessionId)) {
            var register = File.Exists(GrokSubagents.ChildTranscript(parentTranscript, spawned.ChildSessionId));
            await FinishAsync(sessionId, parentTranscript, spawned.ChildSessionId, spawned.SubagentType, register);
        }
    }

    async Task<bool> StartAsync(string sessionId, string agentId, string agentType, string transcript) {
        try {
            using var client  = await http.ForBackgroundAsync();
            var       payload = GeminiSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, transcript);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{profiles.Resolution.ServerUrl}/hooks/subagent-start", content, time);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    async Task<int> PostStopAsync(string sessionId, string agentId, string agentType, string transcript) {
        using var client  = await http.ForBackgroundAsync();
        var       payload = GeminiSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, transcript);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp    = await client.PostWithRetryAsync($"{profiles.Resolution.ServerUrl}/hooks/subagent-stop", content, time);
        return (int)resp.StatusCode;
    }

    static async Task SafeAsync(Func<Task> op) {
        try { await op(); } catch { /* best effort; kcap import --grok recovers anything missed */ }
    }
}
