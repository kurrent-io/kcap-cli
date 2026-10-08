using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Mistral Vibe CLI hooks (<c>kcap hook --mistral-vibe</c>). Vibe's hook model
/// is tool/turn-scoped — <c>pre_tool</c> / <c>post_tool</c> / <c>post_agent</c> — with NO
/// session-start or session-end event. So kcap treats the FIRST hook it sees for a session as that
/// session's start: it POSTs session-start and spawns the transcript watcher, gated on the watcher
/// not already running so the repo-detection work happens once per session rather than on every tool
/// call. Tool content is taken from the transcript the watcher tails, not from the hook payload; for a
/// unified-store session every hook first appends the entries finished since the last one.
/// </summary>
/// <remarks>
/// Vibe's hook stdout contract is forgiving, unlike Gemini's: exit 0 with EMPTY stdout is a
/// passthrough (no decision), so this command writes nothing to stdout and always exits 0 — recording
/// never gates or denies a tool call. (A non-zero exit under a <c>strict</c> hook would deny, which is
/// why <c>hook</c> is a <c>CrashReporter.FailOpenCommand</c>; kcap also never installs its hooks as
/// strict.)
///
/// <para><b>Certification note:</b> Vibe exposes no end-of-session hook, so clean finalization relies
/// on the watcher's own parent-exit fallback (it claims the agent pid for a single-session vendor and
/// finalizes when the agent exits). The exact hook payload field names and the end-of-session
/// behaviour must be confirmed against a real <c>vibe</c> binary; <c>parent_session_id</c> subagent
/// handling is deferred until that session layout is known.</para>
/// </remarks>
sealed class MistralVibeHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, HostedAgent hosted, ICapacitorHttpClient http, WatcherManager watchers,
        GitProviderRouter router) {
    readonly AgentHookPoster _poster = new(config, profiles, http, watchers, clock.Time);

    string Url => profiles.Resolution.ServerUrl!;

    public async Task<int> Handle(TextReader stdin) {
        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { return 0; } // never crash the host CLI on a malformed payload
        if (node is null) return 0;

        try {
            return await DispatchAsync(node);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            // Recording must never fail the hook. Diagnostics go to stderr, which Vibe reads only as a
            // failure diagnostic and ignores on the exit-0 success path.
            await Console.Error.WriteLineAsync($"[kcap] vibe hook failed: {ex.Message}");
            return 0;
        }
    }

    async Task<int> DispatchAsync(JsonNode node) {
        // Vibe session ids are dashed UUIDs; keep the dashless form for the server (the
        // AgentSession-{dashless} convention every vendor shares).
        var dashedSessionId = TryGetString(node, "session_id");
        if (string.IsNullOrEmpty(dashedSessionId) || !Guid.TryParse(dashedSessionId, out _)) return 0;
        var sessionId = dashedSessionId.Replace("-", "");

        // `kcap disable` must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) return 0;

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = profiles.Effective;

        if (PathExclusion.IsOutOfScope(cwd, activeProfile?.AllowedPaths, activeProfile?.ExcludedPaths, home))
            return 0;

        var transcriptPath = TranscriptToTail(node, sessionId);
        if (transcriptPath is null) return 0;

        // Vibe has no session-start event, so the first hook we see starts recording; every later hook
        // only brings the tailed transcript up to date.
        if (watchers.IsWatcherAlive(sessionId)) return 0;

        await StartRecording(node, sessionId, cwd, activeProfile, transcriptPath);
        return 0;
    }

    /// <summary>The file the watcher tails. The legacy store's <c>transcript_path</c> is that file
    /// already; the unified store's is the session directory, so its finished entries are copied into
    /// a JSONL file first.</summary>
    string? TranscriptToTail(JsonNode node, string sessionId) {
        var transcriptPath = TryGetString(node, "transcript_path");
        if (string.IsNullOrEmpty(transcriptPath)) return null;
        if (!Directory.Exists(transcriptPath)) return transcriptPath;

        var live = MistralVibeLiveTranscript.PathFor(config, sessionId);
        MistralVibeLiveTranscript.Follow(transcriptPath, live,
            MistralVibeConfigToml.ConfiguredModel(harnesses.Of<MistralVibeHarness>().Paths.ConfigToml));
        return live;
    }

    async Task StartRecording(JsonNode node, string sessionId, string? cwd, Profile? activeProfile, string transcriptPath) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = "startup",
            ["home_dir"]        = home.Path,
            // The generic session-start route binds a Claude-shaped record, which requires both.
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = cwd ?? "",
        };

        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;

        if (TryGetIsoTimestamp(node, "timestamp") is { } startedAt) forwarded["started_at"] = startedAt.ToString("O");
        if (hosted.AgentId is { } agentHostId) forwarded["agent_host_id"] = agentHostId;
        if (activeProfile?.DefaultVisibility is { } visibility) forwarded["default_visibility"] = visibility;

        SessionStartInventory.Stamp(forwarded, config, harnesses, clock.Time);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(router, config, forwarded.ToJsonString(), clock.Time);

        if (await RepoExclusion.IsOutOfScopeAsync(router, config, enriched,
                                                  activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time)) {
            DisabledSessions.Mark(sessionId, config);
            return;
        }

        var spool   = new HookSpool(config, clock.Time);
        var outcome = await _poster.PostOrSpoolAsync("session-start/mistral-vibe", enriched, "mistral-vibe-hook",
            spool, sessionId, route: "session-start/mistral-vibe");

        // Spawn on Posted OR Spooled (an auth lapse / outage must not withhold the watcher); only a
        // permanent failure skips it, and the next hook retries.
        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return;

        await watchers.EnsureWatcherRunning(sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: false, vendor: "mistral-vibe");
    }

    static DateTimeOffset? TryGetIsoTimestamp(JsonNode? node, string fieldName) =>
        node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)
     && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    static string? TryGetString(JsonNode? node, string fieldName) =>
        node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
