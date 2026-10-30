using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Dispatcher for the SST OpenCode live-ingest plugin. OpenCode has no
/// shell hooks; the shipped <c>kcap.ts</c> plugin invokes:
///   <c>kcap hook --opencode --event session-start --session &lt;id&gt; --file &lt;path&gt; [--cwd &lt;cwd&gt;] [--model &lt;m&gt;] [--provider &lt;p&gt;] [--version &lt;v&gt;]</c>
///
/// Wire contract (mirrors <see cref="KiroHookCommand"/> — OpenCode likewise has no
/// session-end signal): session-start → POST /hooks/session-start/opencode, then
/// ensure the watcher is running (vendor=opencode) tailing the JSONL file the
/// plugin writes. The watcher owns session-end: <c>GetCodingAgentPid("opencode")</c>
/// passes the opencode pid as <c>--parent-pid</c>, so the watcher POSTs
/// /hooks/session-end/opencode when opencode exits. The plugin re-fires
/// session-start cheaply on each session.idle — the server's deterministic
/// lifecycle id collapses the repeats and <see cref="WatcherManager.EnsureWatcherRunning"/>
/// is a no-op once live.
///
/// <para><b>stdout is a DATA channel on session-start</b>, carrying the team-memory fragment (raw
/// text, no envelope) for the plugin to append to the model's system prompt. Diagnostics go to stderr.
/// Every other event keeps writing nothing at all.</para>
///
/// Fail-open throughout — a kcap/server problem must never disrupt the OpenCode session.
/// </summary>
sealed class OpenCodeHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, HostedAgent hosted, ICapacitorHttpClient http, WatcherManager watchers,
        GitProviderRouter router, WorkingDirectory workdir) {
    readonly SessionStartAudience audience = SessionStartAudience.From(hosted);

    readonly AgentHookPoster _poster = new(config, profiles, http, watchers, clock.Time);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>The plugin discards a <c>kcap hook</c> answer slower than its own 10s race
    /// (<c>OpenCodeExtensionInstaller</c>); this stops well inside it so session-start never sits on
    /// the agent's critical path.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    public Task<int> Handle(string[] args) => Handle(args, Console.Out);

    /// <param name="stdout">Injected so the fragment the plugin consumes is assertable without
    /// capturing the process's real console — which is also the only way to test the no-fragment path,
    /// since "wrote nothing" is indistinguishable from "was never called" on a shared writer.</param>
    internal async Task<int> Handle(string[] args, TextWriter stdout) {
        var eventName = GetArg(args, "--event");
        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --opencode requires --event <session-start> "
              + "(the kcap OpenCode plugin passes it; re-run: kcap plugin install --opencode)");
            return 1;
        }

        var sessionIdRaw = GetArg(args, "--session");
        if (string.IsNullOrWhiteSpace(sessionIdRaw)) return 0;

        // OpenCode ids ("ses_…") carry no dashes; keep the raw form for the server
        // payload and a dashless form for local keys (mirrors every vendor dispatcher).
        var sessionId = sessionIdRaw.Replace("-", "");

        var file = GetArg(args, "--file");
        if (string.IsNullOrWhiteSpace(file)) return 0; // no transcript path — nothing to tail

        var cwd = GetArg(args, "--cwd");

        // Mirror the disabled-session fast path: `kcap disable` must stop every POST
        // and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) return 0;

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(config, clock.Time);

        var activeProfile = profiles.Effective;

        if (PathExclusion.IsOutOfScope(cwd, activeProfile?.AllowedPaths,
                                      activeProfile?.ExcludedPaths, home)) {
            return 0;
        }

        // session-start is the only actionable event; the watcher owns session-end.
        return eventName == "session-start"
            ? await HandleSessionStart(sessionId, sessionIdRaw, file, cwd, args, activeProfile, spool, stdout, clock.Budget(Ceiling))
            : 0;
    }

    async Task<int> HandleSessionStart(
            string    sessionId,
            string    sessionIdRaw,
            string    file,
            string?   cwd,
            string[]  args,
            Profile?  activeProfile,
            HookSpool spool,
            TextWriter stdout,
            HookBudget budget
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionIdRaw,
            ["home_dir"]        = home.Path,
            ["started_at"]      = clock.Time.GetUtcNow().ToString("O")
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }
        if (GetArg(args, "--model")    is { } model)    forwarded["model"]            = model;
        if (GetArg(args, "--provider") is { } provider) forwarded["provider_id"]      = provider;
        if (GetArg(args, "--version")  is { } version)  forwarded["opencode_version"] = version;

        if (hosted.AgentId is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the JsonString
        // round-trip (same rationale as the Kiro/Copilot dispatchers); null lets the
        // server fall back to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        SessionStartInventory.Stamp(forwarded, config, harnesses, clock.Time);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(router, config, forwarded.ToJsonString(), clock.Time);

        if (await RepoExclusion.IsOutOfScopeAsync(router, config, enriched,
                                                  activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Scope root: the git root stamped onto `forwarded` above when one was found, else the payload
        // cwd. Read back from `enriched` so the fallback matches exactly what the server received.
        // Never a process-cwd fallback — see StartMemoryIndexTask's scope-safety note.
        var scopeRoot = ScopeRootFrom(enriched, cwd);

        // Start the memory fetch so it OVERLAPS the lifecycle POST. Never before it, and never awaited
        // before it — the POST is what capture depends on.
        //
        // Gated on the caller DECLARING it can consume a fragment: an older plugin discards stdout, so a
        // fetch for it is wasted, and the work-items nudge, which rides the same channel, must not reach
        // it as raw text.
        var canConsumeFragment = MemoryContractOf(args) >= 1;
        var memoryTask = canConsumeFragment
            ? StartMemoryIndexTask(sessionId, scopeRoot,
                activeProfile?.DisableMemoryIndex is true || !audience.GetsDriverGuidance,
                activeProfile?.DisableSessionGuidelines is true,
                SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.OpenCode, harnesses, audience: audience),
                budget.Remaining)
            : Task.FromResult<string?>(null);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure skips the
        // watcher this firing; the next session.idle retries.
        var outcome = await _poster.PostOrSpoolAsync("session-start/opencode", enriched, "opencode-hook",
            spool, sessionId, route: "session-start/opencode");

        // BEFORE the watcher gate below, and before any early return: the plugin reads stdout
        // regardless of what the watcher did, so a withheld watcher must not suppress the injection.
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        var workItemsNudge = canConsumeFragment
            ? HarnessNudgeEmitter.Combine(
                SessionNudges.Resolve(HarnessId.OpenCode, sessionId, activeProfile, harnesses, PlanEntitlementStore.Get(Url, config, clock.Time.GetUtcNow()), audience, cwd, home),
                // Inside the gate, unlike the harness nudge below: resolving takes the one-shot
                // marker, and an older plugin discards this stdout, so outside it the notice would be
                // spent on a session that never shows it.
                FirstRunNoticeEmitter.Resolve(activeProfile?.DisableFirstRunNotice is true || !audience.GetsOnboarding, config, HarnessId.OpenCode, harnesses))
            : null;
        // The harness nudge is outside the contract gate: it has its own 6h evaluation throttle.
        var combinedNudge = HarnessNudgeEmitter.Combine(
            workItemsNudge, HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true || !audience.GetsOnboarding, config, harnesses, clock.Time));
        await WriteMemoryFragment(stdout, fragment, combinedNudge);

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return 0;

        await watchers.EnsureWatcherRunning(sessionId, file,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "opencode"
        );

        return 0;
    }

    /// <summary>
    /// Writes the team-memory fragment as OpenCode's plugin consumes it: raw text, no envelope.
    ///
    /// <para><b>A null fragment writes ZERO BYTES.</b> This hook emitted nothing at all before the
    /// memory index existed, and the plugin treats any non-empty stdout as a fragment — so emitting a
    /// placeholder would have it append an empty system entry to every request.</para>
    /// </summary>
    internal static async Task WriteMemoryFragment(TextWriter stdout, string? fragment, string? workItemsNudge = null) {
        var payload = RenderMemoryOutput(fragment, workItemsNudge);
        if (payload.Length == 0) return;

        await stdout.WriteAsync(payload);
        await stdout.FlushAsync();
    }

    /// <summary>The exact bytes stdout receives. Pure, so the zero-bytes rule is assertable without a
    /// writer — "wrote nothing" and "was never called" are otherwise indistinguishable.</summary>
    internal static string RenderMemoryOutput(string? fragment, string? workItemsNudge = null) =>
        fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)
            ? ""
            : SessionStartMemoryOutputAdapters.Render(HarnessId.OpenCode, fragment, workItemsNudge);

    /// <summary>
    /// The lifecycle this harness reports. <c>CallbackMayRepeat</c> because a restart or a resumed session
    /// fires the plugin's start again for the same session id; <c>HostKeepsContext</c> is false because
    /// the plugin holds the fragment only in its own process, so each of those starts needs it again.
    /// Within one process the plugin keeps the first fragment, so a repeat never injects twice.
    ///
    /// <para><c>IsTopLevel</c>/<c>ClassificationAuthoritative</c> are true because the plugin only ever
    /// invokes this command for a session it has PROVEN top-level — its classifier defers on an
    /// ambiguous parentage rather than guessing, so a child session never reaches here at all. That is a
    /// property of the plugin's existing fail-closed classification, not an assumption made here.</para>
    /// </summary>
    internal static SessionMemoryLifecycle LifecycleFor(string sessionId) =>
        new(HarnessId.OpenCode, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true, HostKeepsContext: false);

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as zero bytes.
    ///
    /// <para><b>Scope safety:</b> git root preferred, payload cwd as fallback; with neither, injection
    /// is skipped rather than letting the shared resolver fall back to the hook PROCESS's cwd — which
    /// for OpenCode is wherever the plugin's shell-out inherited, and would inject an unrelated
    /// repository's memories.</para>
    ///
    /// <para>The URL is checked BEFORE any client is constructed, so an unusable base url starts no
    /// task.</para>
    /// </summary>
    internal async Task<string?> StartMemoryIndexTask(
            string     sessionId,
            string?    scopeRoot,
            bool       disabled,
            bool       guidelinesDisabled,
            bool       flowsDisabled,
            TimeSpan   budget) {
        // All three lanes off ⇒ nothing to fetch; any one enabled lane still runs.
        if ((disabled && guidelinesDisabled && flowsDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(router, config, workdir, http.ForMemoryAsync, clock.Time);

            return await new SessionStartMemoryOrchestrator(store, provider, clock.Time).GetFragmentAsync(
                LifecycleFor(sessionId),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled, WakeUpsWanted: hosted.IsHosted, FlowsDisabled: flowsDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>Total by construction: <c>GetValue&lt;string&gt;</c> THROWS on a non-string node, and this
    /// runs on a fail-open hook path whose only job is to find a scope root.</summary>
    static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The scope root, or null when neither a git root nor a payload cwd is available. Parsing
    /// is guarded because a malformed enrichment result must degrade to "no injection", never to an
    /// exception on the lifecycle path.</summary>
    static string? ScopeRootFrom(string enriched, string? cwd) {
        try {
            return AsString(JsonNode.Parse(enriched)?["workspace_root"]) ?? cwd;
        } catch {
            return cwd;
        }
    }

    /// <summary>
    /// The memory-delivery contract version the CALLER declares, or 0 when it declares none.
    ///
    /// <para>Absence means 0 — an older plugin, which discards this command's stdout — so no fetch
    /// happens. An unparseable value is also 0: a caller that cannot state a
    /// version it understands is not one to hand a fragment to.</para>
    ///
    /// <para>The reverse pairing (new plugin, older binary) needs nothing: this command has always
    /// ignored unrecognised arguments, so the flag is inert there and the plugin simply receives no
    /// fragment — fail-open in the direction that matters.</para>
    /// </summary>
    internal static int MemoryContractOf(string[] args) =>
        int.TryParse(GetArg(args, "--memory-contract"), out var version) ? version : 0;

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
