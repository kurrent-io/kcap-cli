using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// <c>kcap hook --grok</c>, run by Grok Build for the events <see cref="GrokHooksInstaller"/> registers.
/// Session start posts the session and starts the watcher on the session's <c>updates.jsonl</c>; Stop
/// restarts a watcher that is gone. The watcher ends the session itself once Grok exits.
/// </summary>
/// <remarks>
/// Grok reads a passive hook's exit code and nothing else, and a non-zero exit from Stop could hold the
/// turn open, so this always exits 0 and writes nothing to stdout.
/// </remarks>
sealed class GrokHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, ICapacitorHttpClient http, WatcherManager watchers, GitProviderRouter router) {
    readonly AgentHookPoster _poster = new(config, profiles, http, watchers, clock.Time);

    string Url => profiles.Resolution.ServerUrl!;

    public async Task<int> Handle(TextReader stdin) {
        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch { return 0; }
        if (node is null) return 0;

        try {
            await DispatchAsync(node);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            await Console.Error.WriteLineAsync($"[kcap] grok hook failed: {ex.Message}");
        }

        return 0;
    }

    async Task DispatchAsync(JsonNode node) {
        if (GrokHostedSession.IsDaemonHosted()) return;

        var dashedSessionId = Str(node, "sessionId");
        if (string.IsNullOrEmpty(dashedSessionId) || !Guid.TryParse(dashedSessionId, out _)) return;

        var sessionId = dashedSessionId.Replace("-", "");
        if (DisabledSessions.IsDisabled(sessionId, config)) return;

        var cwd           = Str(node, "cwd");
        var activeProfile = profiles.Effective;

        if (PathExclusion.IsOutOfScope(cwd, activeProfile?.AllowedPaths, activeProfile?.ExcludedPaths, home)) return;

        switch (Str(node, "hookEventName")) {
            // Session start carries no transcript path, and a cwd too long to encode resolves only once
            // Grok writes its .cwd file, so the start is posted whether or not the file can be found yet.
            case "session_start":
                var derived = cwd is null ? null : harnesses.Of<GrokHarness>().Paths.UpdatesJsonl(cwd, dashedSessionId);
                await StartRecording(node, sessionId, cwd, activeProfile, derived);
                break;
            case "stop" when Str(node, "transcriptPath") is { } named && !watchers.IsWatcherAlive(sessionId):
                await EnsureWatcher(sessionId, named, cwd, skipTitle: true);
                break;
        }
    }

    async Task StartRecording(JsonNode node, string sessionId, string? cwd, Profile? activeProfile, string? transcriptPath) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["cwd"]             = cwd ?? "",
            ["home_dir"]        = home.Path,
        };

        if ((Str(node, "workspaceRoot") ?? (cwd is null ? null : GitRepository.FindRoot(cwd))) is { } root) forwarded["workspace_root"] = root;
        if (Timestamp(node) is { } startedAt) forwarded["started_at"]         = startedAt.ToString("O");
        if (activeProfile?.DefaultVisibility is { } visibility) forwarded["default_visibility"] = visibility;

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(router, config, forwarded.ToJsonString(), clock.Time);

        if (await RepoExclusion.IsOutOfScopeAsync(router, config, enriched,
                                                  activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time)) {
            DisabledSessions.Mark(sessionId, config);
            return;
        }

        var outcome = await _poster.PostOrSpoolAsync("session-start/grok", enriched, "grok-hook",
            new HookSpool(config, clock.Time), sessionId, route: "session-start/grok");

        // A refused start leaves the session unrecorded, so a later Stop must not start capturing it.
        if (outcome == HookPostOutcome.Failed) DisabledSessions.Mark(sessionId, config);
        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url) || transcriptPath is null) return;

        // A resumed session already has its title.
        await EnsureWatcher(sessionId, transcriptPath, cwd, skipTitle: Str(node, "source") is "load" or "resume");
    }

    Task EnsureWatcher(string sessionId, string transcriptPath, string? cwd, bool skipTitle) =>
        watchers.EnsureWatcherRunning(sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: skipTitle, vendor: "grok");

    static DateTimeOffset? Timestamp(JsonNode node) =>
        Str(node, "timestamp") is { } s
     && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    static string? Str(JsonNode node, string field) =>
        node[field] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
