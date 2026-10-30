using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Dispatcher for Muse Code hooks (<c>kcap hook --muse</c>). Muse's payloads are Claude-shaped but carry
/// a null <c>transcript_path</c>, so the log is found from the session id and the date. A child's hooks
/// carry the child's own id as <c>session_id</c>; its parent is named only by <c>MUSE_SESSION_ID</c>.
/// </summary>
/// <remarks>Muse appends plain SessionStart stdout to the model's context, so the memory fragment is the
/// only thing ever written there, and every path exits 0.</remarks>
sealed class MuseHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, HostedAgent hosted, ICapacitorHttpClient http, WatcherManager watchers,
        GitProviderRouter router, WorkingDirectory workdir) {
    /// <summary>Under Muse's 16 KiB hook stdout cap, past which it discards the output whole.</summary>
    internal const int StdoutCapBytes = 15 * 1024;

    const string Vendor = "muse";

    // Muse kills a hook after 5 s, or 15 s on SessionEnd in the TUI; `muse exec` gives SessionEnd well
    // under a second, which is why that hook hands its end to the watcher instead of posting it.
    static readonly TimeSpan Ceiling           = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SessionEndCeiling = TimeSpan.FromSeconds(15);

    readonly AgentHookPoster _poster = new(config, profiles, http, watchers, clock.Time);

    string? Url => profiles.Resolution.ServerUrl;

    MusePaths Paths => harnesses.Of<MuseHarness>().Paths;

    public Task<int> Handle(TextReader stdin) =>
        Handle(stdin, Console.Out, Environment.GetEnvironmentVariable, DateOnly.FromDateTime(clock.Time.GetLocalNow().DateTime));

    internal async Task<int> Handle(TextReader stdin, TextWriter stdout, Func<string, string?> env, DateOnly today) {
        JsonNode? node;
        try { node = JsonNode.Parse(await stdin.ReadToEndAsync()); }
        catch (Exception ex) when (ex is JsonException or IOException) { return 0; }

        if (node is null) return 0;

        try {
            await DispatchAsync(node, stdout, env, today);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            await Console.Error.WriteLineAsync($"[kcap] muse hook failed: {ex.Message}");
        }

        return 0;
    }

    /// <summary>The top-level session a child hook belongs to, dashless; null when the variable is
    /// absent or not a UUID.</summary>
    internal static string? ParentSessionId(Func<string, string?> env) =>
        Guid.TryParse(env("MUSE_SESSION_ID"), out var parent) ? parent.ToString("N") : null;

    /// <summary>The fragment unchanged when it fits; otherwise its first line (the marker) and as many
    /// whole following lines as fit, so an over-long fragment degrades rather than being dropped.</summary>
    internal static string? CapFragment(string? fragment, int maxBytes) {
        if (fragment is null || Encoding.UTF8.GetByteCount(fragment) <= maxBytes) return fragment;

        var lines = fragment.Split('\n');
        var kept  = new StringBuilder();
        var bytes = 0;

        foreach (var line in lines) {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > maxBytes) break;

            kept.Append(line).Append('\n');
            bytes += size;
        }

        return kept.Length == 0 ? null : kept.ToString();
    }

    async Task DispatchAsync(JsonNode node, TextWriter stdout, Func<string, string?> env, DateOnly today) {
        var eventName = TryGetString(node, "hook_event_name");
        if (eventName is not ("SessionStart" or "SessionEnd" or "Stop" or "SubagentStart" or "SubagentStop")) return;

        if (!Guid.TryParse(TryGetString(node, "session_id"), out var hookSession)) return;

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = profiles.Effective;

        if (PathExclusion.IsOutOfScope(cwd, activeProfile?.AllowedPaths, activeProfile?.ExcludedPaths, home)) return;

        var parent   = ParentSessionId(env);
        var topLevel = parent is null || parent == hookSession.ToString("N");

        switch (eventName) {
            // A child's own lifecycle hooks are not a second top-level session.
            case "SessionStart" when topLevel:
                await HandleSessionStart(node, hookSession, cwd, activeProfile, stdout, today);
                break;
            case "Stop" when topLevel:
                await Rearm(hookSession, cwd, activeProfile, clock.Budget(Ceiling), today);
                break;
            case "SessionEnd" when topLevel:
                await HandleSessionEnd(node, hookSession, cwd, today);
                break;
            case "SubagentStart" or "SubagentStop" when parent is not null:
                await HandleSubagent(node, Guid.Parse(parent), eventName == "SubagentStart", clock.Budget(Ceiling), today);
                break;
        }
    }

    async Task HandleSessionStart(JsonNode node, Guid session, string? cwd, Profile? activeProfile, TextWriter stdout, DateOnly today) {
        var source = TryGetString(node, "source") is { Length: > 0 } s ? s : "startup";
        var budget = clock.Budget(Ceiling);

        if (source is not ("startup" or "fork")) {
            await Rearm(session, cwd, activeProfile, budget, today);
            return;
        }

        var sessionId = session.ToString("N");
        if (DisabledSessions.IsDisabled(sessionId, config)) return;

        // Each session needs its own repository event, even in a cwd another session already reported.
        if (cwd is not null) RepositoryDetection.ClearLastEmitted(config, cwd);

        if (await IsRepoExcludedAsync(sessionId, cwd, activeProfile, budget)) return;

        // Spawn before the POST, as Claude does: a hook Muse kills mid-POST must not take capture with it.
        var log = Paths.ResolveSessionLog(session.ToString("D"), today);
        if (log is not null && HookHttp.IsPostable(Url)) {
            DiscardHandedEnd(sessionId);
            await watchers.EnsureWatcherRunning(sessionId, log,
                agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: false, vendor: Vendor);
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = source,
            ["home_dir"]        = home.Path,
            ["cwd"]             = cwd ?? "",
            ["started_at"]      = clock.Time.GetUtcNow().ToString("O"),
        };

        if (log is not null) forwarded["transcript_path"] = log;
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        var marker      = MuseHostedSessionMarker.TryRead(home, sessionId);
        var agentHostId = hosted.AgentId ?? marker?.AgentId;
        var markerKind  = HostedSessionKinds.Parse(marker?.SessionKind);
        var audience    = SessionStartAudience.From(hosted with {
            AgentId = agentHostId,
            Kind    = markerKind == HostedSessionKind.None ? hosted.Kind : markerKind
        });
        if (agentHostId is not null) forwarded["agent_host_id"] = agentHostId;
        if (activeProfile?.DefaultVisibility is { } visibility) forwarded["default_visibility"] = visibility;

        SessionStartInventory.Stamp(forwarded, config, harnesses, clock.Time);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(
            router, config, forwarded.ToJsonString(), clock.Time, budget.Remaining, detectPullRequest: false);

        var memoryTask = StartMemoryIndexTask(sessionId, ScopeRootFrom(enriched, cwd),
            disabled: activeProfile?.DisableMemoryIndex is true || !audience.GetsDriverGuidance,
            guidelinesDisabled: activeProfile?.DisableSessionGuidelines is true,
            flowsDisabled: SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Muse, harnesses, audience: audience),
            source,
            wakeUpsWanted: audience.IsHosted,
            budget.Remaining);

        // Posted with or without a log: Stop starts a missing watcher but never posts the start.
        await PostInOrderAsync("session-start/muse", enriched, sessionId, budget);

        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        var nudge = HarnessNudgeEmitter.Combine(
            SessionNudges.Resolve(HarnessId.Muse, sessionId, activeProfile, harnesses, PlanEntitlementStore.Get(Url, config, clock.Time.GetUtcNow()), audience, cwd, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true || !audience.GetsOnboarding, config, harnesses, clock.Time),
            FirstRunNoticeEmitter.Resolve(activeProfile?.DisableFirstRunNotice is true || !audience.GetsOnboarding, config, HarnessId.Muse, harnesses));
        await WriteFragment(stdout, fragment, nudge);
    }

    /// <summary>Starts the session's watcher when none is running, without posting: a resume, or a
    /// turn ending after a SessionStart that was killed or found no log.</summary>
    async Task Rearm(Guid session, string? cwd, Profile? activeProfile, HookBudget budget, DateOnly today) {
        var sessionId = session.ToString("N");
        if (DisabledSessions.IsDisabled(sessionId, config) || !HookHttp.IsPostable(Url)) return;

        // Both whatever the watcher's state: the one still alive may be leaving with the old end, and
        // it decides whether to end the session by whether a live process is claimed for it.
        watchers.ClaimAgentProcess(sessionId, Vendor);
        DiscardHandedEnd(sessionId);
        if (watchers.IsWatcherAlive(sessionId)) return;

        if (Paths.ResolveSessionLog(session.ToString("D"), today) is not { } log) return;
        if (await IsRepoExcludedAsync(sessionId, cwd, activeProfile, budget)) return;

        await watchers.EnsureWatcherRunning(sessionId, log,
            agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: true, vendor: Vendor);
    }

    /// <summary>A session carrying on makes any end handed over before now stale. The mark covers a claim a
    /// watcher already holds, which it drops before posting and arms a successor; the claim here takes an
    /// end nobody has, so it cannot end the watcher armed next. An end handed over after the mark is the
    /// resumed run's own and goes back for that watcher.</summary>
    void DiscardHandedEnd(string sessionId) {
        var paths = WatcherPaths.FromEnvironment(config);

        MuseSessionEndRequest.MarkResumed(paths, sessionId);

        if (MuseSessionEndRequest.TryClaim(paths, sessionId, "rearm") is not { } handed) return;

        if (MuseSessionEndRequest.ResumedSince(paths, sessionId, handed.HandedAt))
            MuseSessionEndRequest.Release(handed.ClaimedPath);
        else
            MuseSessionEndRequest.Unclaim(paths, sessionId, handed);
    }

    /// <summary>An out-of-scope repository disables the session, so its later hooks stop at the
    /// disabled check.</summary>
    async Task<bool> IsRepoExcludedAsync(string sessionId, string? cwd, Profile? activeProfile, HookBudget budget) {
        var body = new JsonObject { ["cwd"] = cwd }.ToJsonString();
        if (!await RepoExclusion.IsOutOfScopeAsync(router, config, body,
                activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time, budget.Remaining))
            return false;

        DisabledSessions.Mark(sessionId, config);
        return true;
    }

    /// <summary>Muse documents 15 s for SessionEnd but <c>muse exec</c> kills the hook inside a second, so
    /// a session with a log never has its end posted from here: the hook writes the request it would have
    /// posted and makes sure a watcher is running to drain the log and post it. A hook killed while posting
    /// would take the only copy of the end with it; a watcher is a detached process. Only a session with
    /// no log, which has nothing to drain, is posted directly.</summary>
    async Task HandleSessionEnd(JsonNode node, Guid session, string? cwd, DateOnly today) {
        var sessionId = session.ToString("N");
        var budget    = clock.Budget(SessionEndCeiling);

        if (DisabledSessions.IsDisabled(sessionId, config)) {
            DisabledSessions.RemoveMarker(sessionId, config);
            return;
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = TryGetString(node, "reason") is { Length: > 0 } reason ? reason : "other",
            ["home_dir"]        = home.Path,
            ["ended_at"]        = clock.Time.GetUtcNow().ToString("O"),
        };

        if (cwd is not null) forwarded["cwd"] = cwd;
        if ((hosted.AgentId ?? MuseHostedSessionMarker.TryRead(home, sessionId)?.AgentId) is { } agentHostId)
            forwarded["agent_host_id"] = agentHostId;

        if (Paths.ResolveSessionLog(session.ToString("D"), today) is { } log && HookHttp.IsPostable(Url)) {
            MuseSessionEndRequest.Write(WatcherPaths.FromEnvironment(config), sessionId, forwarded.ToJsonString());

            // A watcher alive here takes the request on its next tick or on its last look, which it makes
            // only after retiring its pid file; one that has gone is replaced by a watcher armed for it.
            await watchers.EnsureWatcherRunning(sessionId, log,
                agentId: null, sessionIdOverride: null, cwd: cwd, skipTitle: true, vendor: Vendor);
            MuseHostedSessionMarker.Delete(home, sessionId);

            return;
        }

        await PostInOrderAsync("session-end/muse", forwarded.ToJsonString(), sessionId, budget);
        MuseHostedSessionMarker.Delete(home, sessionId);
    }

    /// <summary>No child watcher starts here: the parent watcher's scan tails a child once its log
    /// exists. A worker takes the label the parent log gives it, so the hook and the scan agree.</summary>
    async Task HandleSubagent(JsonNode node, Guid parent, bool isStart, HookBudget budget, DateOnly today) {
        var parentId = parent.ToString("N");
        if (DisabledSessions.IsDisabled(parentId, config)) return;

        if (!Guid.TryParse(TryGetString(node, "child_session_id"), out var child) || child == parent) return;
        if (Paths.ResolveSessionLog(parent.ToString("D"), today) is not { } parentLog) return;

        var childId    = child.ToString("D");
        var subagentId = TryGetString(node, "subagent_id");
        var agentType  = string.IsNullOrWhiteSpace(subagentId) ? "subagent"
            : MuseSubagentDiscovery.IsReminderAgentId(subagentId) ? subagentId.Trim()
            : WorkerLabel(parentLog, childId);

        var agentId  = child.ToString("N");
        var childLog = MusePaths.ChildSessionLogPath(parentLog, childId);

        var payload = isStart
            ? MuseSubagentDiscovery.BuildStartPayload(parentId, agentId, agentType, childLog)
            : MuseSubagentDiscovery.BuildStopPayload(parentId, agentId, agentType, childLog);

        await PostInOrderAsync(isStart ? "subagent-start" : "subagent-stop", payload.ToJsonString(), parentId, budget);
    }

    // The scan's "subagent" means unannounced; a Guid subagent_id already says it is a worker.
    static string WorkerLabel(string parentLog, string childId) {
        var type = MuseSubagentDiscovery.LabelFor(childId, MuseSubagentDiscovery.ResolveChildLabels(parentLog)).AgentType;
        return type == "subagent" ? "worker" : type;
    }

    /// <summary>Spools behind an undelivered backlog for the session rather than overtaking it, so a
    /// stranded session-start still reaches the server first; a spent budget spools too.</summary>
    async Task<HookPostOutcome> PostInOrderAsync(string route, string body, string sessionId, HookBudget budget) {
        var spool = new HookSpool(config, clock.Time);

        if (spool.HasBacklog(sessionId) || budget.Remaining <= TimeSpan.Zero)
            return spool.Append(sessionId, route, body) ? HookPostOutcome.Spooled : HookPostOutcome.Skipped;

        if (!HookHttp.IsPostable(Url))
            return await _poster.PostOrSpoolAsync(route, body, "muse-hook", spool, sessionId, route);

        // Muse kills the hook at its ceiling, so the POST spools when the budget runs out rather than
        // dying mid-retry. A redelivered payload is harmless: the server keys these events by content.
        var authCap = budget.Remaining < AgentHookPoster.AuthCap ? budget.Remaining : AgentHookPoster.AuthCap;
        return await _poster.PostOrSpoolAsync(() => http.ForHookAsync(), route, body, "muse-hook", spool, sessionId, route,
            authCap, budget: budget);
    }

    static async Task WriteFragment(TextWriter stdout, string? fragment, string? nudge) {
        if (fragment is null && string.IsNullOrWhiteSpace(nudge)) return;

        if (CapFragment(SessionStartMemoryOutputAdapters.Render(HarnessId.Muse, fragment, nudge), StdoutCapBytes) is not { Length: > 0 } capped)
            return;

        await stdout.WriteAsync(capped);
        await stdout.FlushAsync();
    }

    internal static SessionMemoryLifecycle LifecycleFor(string sessionId, string? source) =>
        new(HarnessId.Muse, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionStartMemoryHookSupport.ReasonFor(source), CallbackMayRepeat: false);

    async Task<string?> StartMemoryIndexTask(
            string   sessionId,
            string?  scopeRoot,
            bool     disabled,
            bool     guidelinesDisabled,
            bool     flowsDisabled,
            string?  source,
            bool     wakeUpsWanted,
            TimeSpan budget) {
        if ((disabled && guidelinesDisabled && flowsDisabled) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(router, config, workdir, http.ForMemoryAsync, clock.Time);

            return await new SessionStartMemoryOrchestrator(store, provider, clock.Time).GetFragmentAsync(
                LifecycleFor(sessionId, source),
                new SessionStartMemoryContextRequest(Url!, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled, WakeUpsWanted: wakeUpsWanted, FlowsDisabled: flowsDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    static string? ScopeRootFrom(string enriched, string? cwd) {
        try {
            return TryGetString(JsonNode.Parse(enriched), "workspace_root") ?? cwd;
        } catch (JsonException) {
            return cwd;
        }
    }

    static string? TryGetString(JsonNode? node, string fieldName) =>
        node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
