using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

// ReSharper disable ShortLivedHttpClient

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → POST /hooks/stop (best-effort, 2s cap, swallow-all).
///                       Codex fires Stop at every turn end, not session end;
///                       session-end stays owned by the watcher's
///                       parent-PID monitor. The POST lets the server
///                       emit the idle-wait marker that clears the chat
///                       "working" indicator. HandleStop also refreshes watcher
///                       liveness and emits {"continue":true} for Codex's parser.
///   PermissionRequest → in an envelope-sourced hosted session (KCAP_HOSTED_APPSERVER), yield: the
///                       app-server runtime owns approvals there. Otherwise in a daemon-launched
///                       hosted agent (a loopback bridge named), bounce through the daemon's
///                       LocalPermissionBridge and wait for the dashboard's decision (fail-closed on
///                       bridge errors: deny + exit nonzero). Otherwise the approval policy and judge
///                       answer allow or deny; failing that, POST /hooks/permission-record
///                       (fire-and-forget; CLI emits no decision so Codex's own prompt takes over).
///   UserPromptSubmit  → swallowed (neither vendor consumes them)
///   PreToolUse        → a matching deny rule denies the call, in every launch mode; else nothing
///   PostToolUse       → swallowed (a hosted agent's tool-settled notice still goes to its daemon)
/// </remarks>
sealed class CodexHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, HostedAgent hosted, ICapacitorHttpClient http, WatcherManager watchers,
        GitProviderRouter router, WorkingDirectory workdir) {
    readonly SessionStartAudience audience = SessionStartAudience.From(hosted);

    AgentHookPoster Poster(CodexHookEnvironment env) => new(config, profiles, http, watchers, clock.Time, env.Stderr);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>Codex blocks on this hook's stdout and sets no timeout of its own, so this ceiling is
    /// the only thing standing between a stalled fetch and a stalled agent.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    // Codex's Stop and SessionStart hooks parse stdout as the
    // `stop.command.output` / `session-start.command.output` JSON schema and
    // reject empty bodies with "hook returned invalid stop hook JSON output".
    // `continue: true` is the schema default; emitting it explicitly satisfies
    // the parser without altering behavior.
    const string SessionScopedOutputJson = """{"continue":true}""";

    /// <summary>
    /// Writes the session-scoped handshake JSON Codex's SessionStart/Stop stdout parser requires.
    /// Extracted to a single seam so every call site — the happy path, the disabled/excluded
    /// early-outs, and the fail-open fallback — goes through the same write, and so
    /// <see cref="RunSessionStartHandshakeForTest"/> can order background work strictly after it.
    /// </summary>
    internal static void WriteSessionScopedOutput(TextWriter writer) => writer.Write(SessionScopedOutputJson);

    /// <summary>
    /// The SessionStart write, which — unlike Stop's — may carry a team-memory fragment.
    /// Delegates the envelope to the shared <see cref="SessionStartMemoryOutputAdapters"/> so the
    /// combined shape (<c>continue</c> + <c>hookSpecificOutput.additionalContext</c>) is rendered by
    /// the same vendor-neutral code every harness uses; this adapter owns NO rendering of its own.
    ///
    /// <para><b>Absent-fragment invariant:</b> with <paramref name="fragment"/> null the adapter
    /// returns the byte-for-byte <see cref="SessionScopedOutputJson"/> handshake, so every no-memory
    /// path (opt-out, exclusion, provider failure, budget exhaustion) writes the same bytes.
    /// <c>CodexSessionStartMemoryTests</c> pins that equality.</para>
    ///
    /// <para>The payload is serialized into a string BEFORE the first byte reaches
    /// <paramref name="writer"/>, so a renderer/serializer fault cannot emit a partial rich object
    /// followed by a second (minimal) one — that would break Codex's single-JSON-value contract.
    /// Any such fault degrades to the minimal handshake instead.</para>
    /// </summary>
    internal static void WriteSessionStartOutput(TextWriter writer, string? fragment, string? workItemsNudge = null) {
        string payload;

        try {
            // With no fragment and no nudge, the constant rather than the shared adapter: the adapter
            // appends a trailing newline, and the no-content handshake keeps the exact bytes the
            // handshake test pins. Anything with content goes through the adapter like every harness.
            payload = fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)
                ? SessionScopedOutputJson
                : SessionStartMemoryOutputAdapters.Render(HarnessId.Codex, fragment, workItemsNudge);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            payload = SessionScopedOutputJson;
        }

        writer.Write(payload);
    }

    /// <summary>
    /// Enforces the Codex stdout-first contract — <paramref name="writeStdout"/>
    /// (the synchronous handshake write) always completes before <paramref name="postStdoutWork"/>
    /// (the best-effort watcher-ensure + background spool drain) even starts. The parent Codex
    /// process blocks on the hook's stdout, so a large/unreachable spool backlog sitting behind
    /// <paramref name="postStdoutWork"/> must never delay or gate the write. Production's
    /// <see cref="HandleSessionStart"/> is the only non-test caller, always with real delegates;
    /// <c>CodexStdoutContractTests</c> supplies recording/blocking ones to assert the ordering
    /// without a live server.
    /// </summary>
    internal static async Task RunSessionStartHandshakeForTest(Action writeStdout, Func<Task> postStdoutWork) {
        writeStdout();
        await postStdoutWork();
    }

    /// <summary>
    /// Starts the shared SessionStart memory fetch so it overlaps the lifecycle POST
    /// instead of serializing ahead of it — the same start-early/await-bounded shape
    /// <c>ClaudeHookCommand.StartMemoryIndexTask</c> uses. Returns a task that NEVER faults:
    /// every failure mode resolves to null, which the writer renders as the minimal handshake.
    ///
    /// <para><b>Scope safety:</b> a blank scope root is NOT passed through. The shared scope
    /// resolver would otherwise fall back to the hook PROCESS's cwd and could inject an unrelated
    /// repository's memories (the guard <c>CursorHookCommand.RunMemoryOrchestrationAsync</c>
    /// documents). Codex's payload cwd — and the git root derived from it — are the only
    /// authoritative roots; with neither, injection is skipped entirely.</para>
    ///
    /// <para><b>Once per session:</b> no lifecycle <c>source</c> exists on Codex's SessionStart
    /// payload, so the reason is reported as <see cref="SessionLifecycleReason.New"/>. Re-injection
    /// on a resume of the SAME session id is prevented by the shared lease keyed on
    /// (harness, session id) rather than by a reason we cannot observe.</para>
    /// </summary>
    async Task<string?> StartMemoryIndexTask(
            string?    sessionId,
            string?    scopeRoot,
            bool       disabled,
            bool       guidelinesDisabled,
            bool       flowsDisabled,
            TimeSpan   budget) {
        if ((disabled && guidelinesDisabled && flowsDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero)
            return null;

        // A blank or unacceptable URL must skip injection here, before any client is built,
        // rather than fail later mid-fetch. Mirrors PostBestEffortAsync's guard.
        if (!HookHttp.IsPostable(Url)) return null;

        // Construction itself stays inside the fail-open boundary: store-root validation and
        // the injected client factory can throw synchronously.
        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(router, config, workdir, http.ForMemoryAsync, clock.Time);

            return await new SessionStartMemoryOrchestrator(store, provider, clock.Time).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Codex, sessionId!, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true, SessionLifecycleReason.New,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled, FlowsDisabled: flowsDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Awaits the memory fetch under the budget REMAINING at this instant — never the budget it
    /// was started with. Codex blocks on this hook's stdout, so the wait is capped so the handshake
    /// still lands inside the hook ceiling even when the fetch never returns: the cap is
    /// <see cref="HookBudget.Remaining"/>, which ALREADY reserves <see cref="HookBudget.Safety"/> as
    /// headroom for serialization + the write itself — subtracting it again cut the usable window
    /// from 3.5s to 2s at a fresh hook start. On expiry the already-running fetch is
    /// abandoned (not cancelled mid-flight — its own lease bookkeeping owns that) and null is
    /// returned, so the write degrades to the minimal handshake rather than being delayed.
    /// </summary>
    static async Task<string?> AwaitMemoryFragmentAsync(Task<string?> task, HookBudget budget) {
        try {
            var remaining = budget.Remaining;

            if (remaining <= TimeSpan.Zero)
                return task.IsCompletedSuccessfully ? task.Result : null;

            return await task.WaitAsync(remaining, budget.Time);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    public Task<int> Handle(TextReader stdin) => Handle(stdin, CodexHookEnvironment.FromProcess(Environment.GetEnvironmentVariable));

    internal async Task<int> Handle(TextReader stdin, CodexHookEnvironment env) {
        var body    = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = TryGetString(node, "hook_event_name");

        if (string.IsNullOrWhiteSpace(eventName)) return 0;

        // A kcap-launched headless Codex records nothing, but Codex's stdout contract still holds: its
        // Stop / SessionStart parsers reject empty output, and a missing PermissionRequest answer hangs it.
        if (env.Skip) {
            switch (eventName) {
                case "SessionStart" or "Stop":
                    WriteSessionScopedOutput(env.Stdout);
                    break;
                case "PermissionRequest":
                    // Empty hookSpecificOutput → Codex falls back to its
                    // own approval prompt. See HandlePermissionRequestStub.
                    env.Stdout.Write("{}");
                    break;
            }
            return 0;
        }

        // Normalize session_id to dashless GUID, inject home_dir, and tag the
        // agent host id when running inside a daemon-spawned agent. Mirrors
        // the Claude hook path in Program.cs (including the disabled-session
        // check below); the plan_content branch remains Claude-specific.
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = home.Path;

        var agentHostId = hosted.AgentId;
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        // Ahead of the disabled and exclusion gates: the hosting daemon's hints are local display
        // state, not recording.
        await NotifyToolSettledAsync(node, eventName);

        if (eventName switch { "Stop" => true, "UserPromptSubmit" or "PreToolUse" => false, _ => (bool?) null } is { } waiting)
            await DaemonBridgeRelay.NotifyInputWaitAsync(hosted, "codex", TryGetString(node, "session_id"), TryGetString(node, "cwd"), waiting, clock.Budget(Ceiling));

        // After `kcap disable`, no POST and no watcher restart: the next Stop would otherwise re-send
        // a session whose data was just deleted server-side.
        var disabledSessionId = TryGetString(node, "session_id");
        if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId, config)) {
            if (eventName is "Stop" or "SessionStart") {
                WriteSessionScopedOutput(env.Stdout);
            }
            return 0;
        }

        // Path exclusion is a string-prefix compare against the payload's cwd
        // — cheap, safe to run on every event (including Stop, which fires
        // per turn). Repo exclusion is handled inside HandleSessionStart
        // instead: running it here would call RepoExclusion.IsOutOfScopeAsync,
        // which falls back to DetectRepositoryAsync (multiple git commands +
        // gh pr view) when the payload lacks a repository block — too
        // expensive for the per-turn Stop hook. Doing the repo check once at
        // SessionStart (after enrichment, when the repository block is
        // populated) and marking the session via DisabledSessions lets
        // subsequent events take the existing disabled-session fast path
        // above without paying any git cost.
        var activeProfile = profiles.Effective;

        if (PathExclusion.IsOutOfScope(TryGetString(node, "cwd"), activeProfile?.AllowedPaths,
                                      activeProfile?.ExcludedPaths, home)) {
            EmitFallbackOutput(env, eventName);
            return 0;
        }

        try {
            return eventName switch {
                "SessionStart"      => await HandleSessionStart(node, activeProfile, clock.Budget(Ceiling), env),
                "Stop"              => await HandleStop(node, env),
                "PermissionRequest" => await HandlePermissionRequest(node, env),
                "PreToolUse"        => await HandlePreToolUse(node, env),
                "UserPromptSubmit"
                  or "PostToolUse"  => 0,
                _                   => 0
            };
        } catch (Exception ex) {
            // Fail open, but NOT by leaving stdout empty: unlike Claude, Codex's
            // SessionStart/Stop parser rejects empty output and a missing
            // PermissionRequest response hangs it. If a handler throws (e.g. an
            // IO/permission fault while building the authenticated client), the
            // CLI's top-level guard would exit 0 with empty stdout and Codex would
            // report "invalid hook output". Emit the
            // event-appropriate fallback here first, and record for diagnosis.
            CrashReporter.Record(config, "hook", ex, clock.Time, env.Stderr);
            EmitFallbackOutput(env, eventName);
            return 0;
        }
    }

    // Retires a prompt the daemon still holds once it was answered in Codex's terminal. A finished
    // tool carries its name and input, since Codex's permission prompt has no tool id; the main
    // agent's turn end covers a terminal deny, which runs no tool. A subagent's tool hooks share the
    // parent's session and name the subagent in agent_id; its stop is SubagentStop, never Stop.
    Task NotifyToolSettledAsync(JsonNode node, string eventName) {
        var sessionId  = TryGetString(node, "session_id");
        var cwd        = TryGetString(node, "cwd");
        var subagentId = SubagentIdOf(node);
        var budget     = clock.Budget(Ceiling);
        var sentAt     = clock.Time.GetUtcNow().ToUnixTimeMilliseconds();

        return eventName switch {
            "PostToolUse" when TryGetString(node, "tool_use_id") is { } toolUseId =>
                DaemonBridgeRelay.NotifyToolSettledAsync(hosted, "codex", sessionId, cwd, toolUseId, subagentId, budget,
                    TryGetString(node, "tool_name"), node["tool_input"]?.DeepClone(), sentAt),
            "Stop" when subagentId is null =>
                DaemonBridgeRelay.NotifyToolSettledAsync(hosted, "codex", sessionId, cwd, toolUseId: null, subagentId: null, budget, sentAtMs: sentAt),
            _ => Task.CompletedTask,
        };
    }

    // Dashless, the one form the permission hook's bridge payload and the tool-settled relay share.
    static string? SubagentIdOf(JsonNode node) =>
        TryGetString(node, "agent_id") is { Length: > 0 } id ? id.Replace("-", "") : null;

    // Emit the minimal output Codex's parser requires for the given event — the
    // session-scoped {"continue":true} for SessionStart/Stop, an empty object for
    // PermissionRequest (so Codex's own approval prompt takes over), nothing for
    // the swallowed informational events. Used both when we intentionally skip
    // work (exclusion) and as the fail-open fallback when a handler throws.
    static void EmitFallbackOutput(CodexHookEnvironment env, string eventName) {
        switch (eventName) {
            case "SessionStart" or "Stop":
                // Codex's SessionStart/Stop parser rejects empty stdout.
                WriteSessionScopedOutput(env.Stdout);
                break;
            case "PermissionRequest":
                // Empty hookSpecificOutput → Codex's local approval prompt
                // takes over (matches the KCAP_SKIP=1 branch above).
                env.Stdout.Write("{}");
                break;
        }
    }

    async Task<int> HandleSessionStart(JsonNode node, Profile? activeProfile, HookBudget budget, CodexHookEnvironment env) {
        // Stamp the user's configured default visibility onto the payload
        // BEFORE git enrichment so it survives the JsonString round-trip.
        // /hooks/session-start/codex shares SessionStartHook with the Claude
        // route and the server-side SessionHookHandlers.HandleSessionStart
        // reads hook.DefaultVisibility for both vendors; without this, codex
        // sessions in org repos silently default to org-visible because
        // VisibilityService treats null as "fall back to org visibility".
        if (activeProfile?.DefaultVisibility is { } visibility) {
            node["default_visibility"] = visibility;
        }

        // best-effort git-root discovery, fail-open (omitted when cwd is absent or no
        // repo is found) — see the mirrored ClaudeHookCommand comment for rationale.
        if (TryGetString(node, "cwd") is { } startCwd && GitRepository.FindRoot(startCwd) is { } workspaceRoot) {
            node["workspace_root"] = workspaceRoot;
        }

        SessionStartInventory.Stamp(node.AsObject(), config, harnesses, clock.Time);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(router, config, node.ToJsonString(), clock.Time);

        // Repo exclusion runs here (not above the event switch) so that the
        // repository block is already populated by enrichment — RepoExclusion
        // takes the fast in-payload path and skips the expensive
        // DetectRepositoryAsync fallback. Mark the session via
        // DisabledSessions so subsequent Stop / PermissionRequest events
        // take the existing disabled-session fast path at the top of Handle
        // without paying any git cost.
        if (await RepoExclusion.IsOutOfScopeAsync(router, config, enriched,
                                                  activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time)) {
            var excludedSessionId = TryGetString(node, "session_id");

            if (excludedSessionId is not null) DisabledSessions.Mark(excludedSessionId, config);

            WriteSessionScopedOutput(env.Stdout);
            return 0;
        }

        var enrichedNode = JsonNode.Parse(enriched);
        var sessionId    = TryGetString(enrichedNode, "session_id");

        // spawn-before-post. A lapsed-auth or transient/unreachable failure durably
        // spools the payload instead of dropping it (Spooled) — never AuthLapsed here, unlike the
        // legacy PostAsync path, so ShouldSpawnAfter below can safely spawn on Spooled too.
        // Start the team-memory fetch BEFORE the lifecycle POST so the two overlap; it is awaited
        // (budget-capped) immediately before the stdout write below, which is the only consumer.
        // Deliberately started after the exclusion/disabled early-outs above so an excluded repo
        // never reaches the memory subsystem at all.
        var memoryTask = StartMemoryIndexTask(sessionId,
            // The git root discovered above (stamped onto the node) is preferred; the payload cwd is
            // the fallback. Never a process-cwd fallback — see StartMemoryIndexTask's scope note.
            TryGetString(enrichedNode, "workspace_root") ?? TryGetString(enrichedNode, "cwd"),
            // The EFFECTIVE profile, not the resolution's own: ProfileResolver returns a
            // null Profile whenever --server-url or KCAP_URL wins, and GetActiveProfileAsync (which
            // produced activeProfile) is what falls back to the on-disk active profile. Reading the
            // resolved one silently ignored `disable_memory_index: true` for every KCAP_URL user.
            activeProfile?.DisableMemoryIndex is true || !audience.GetsDriverGuidance,
            activeProfile?.DisableSessionGuidelines is true,
            SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Codex, harnesses, audience: audience),
            // Remaining already reserves Safety — subtracting it again here halved the window.
            budget.Remaining);

        var spool = new HookSpool(config, clock.Time, Url);
        var outcome = await Poster(env).PostOrSpoolAsync("session-start/codex", enriched, "codex-hook", spool,
            sessionId: sessionId ?? "", route: "session-start/codex");

        // After the post, so a fault here cannot lose the session-start record; before the stdout handshake, because
        // Codex runs its first tool once that is written.
        if (sessionId is not null) FreezePolicySnapshot(sessionId, TryGetString(enrichedNode, "workspace_root"));

        // Codex blocks on this hook's stdout, so the handshake goes first, on every outcome a
        // permanent POST rejection included, and the watcher and spool drain only after it. The
        // fragment is resolved beforehand so the write itself stays synchronous.
        var fragment = await AwaitMemoryFragmentAsync(memoryTask, budget);

        // The static work-items nudge, resolved (availability-gated + opt-out) independently
        // of the lease-driven memory/guidelines fragment and merged only at the output layer.
        var workItemsNudge = HarnessNudgeEmitter.Combine(
            SessionNudges.Resolve(HarnessId.Codex, sessionId, activeProfile, harnesses, PlanEntitlementStore.Get(Url, config, clock.Time.GetUtcNow()), audience, TryGetString(enrichedNode, "cwd"), home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true || !audience.GetsOnboarding, config, harnesses, clock.Time),
            FirstRunNoticeEmitter.Resolve(activeProfile?.DisableFirstRunNotice is true || !audience.GetsOnboarding, config, HarnessId.Codex, harnesses));

        await RunSessionStartHandshakeForTest(
            writeStdout: () => WriteSessionStartOutput(env.Stdout, fragment, workItemsNudge),
            postStdoutWork: () => RunPostStdoutWork(spool, enrichedNode, sessionId, outcome, env));

        // Non-zero says the session was not recorded; RunPostStdoutWork spawns no watcher for it.
        return outcome == HookPostOutcome.Failed ? 1 : 0;
    }

    // Runs after Codex's stdout handshake and must never delay it. The spool drain is
    // fire-and-forget: the daemon's periodic drain carries reliable delivery. An envelope-sourced
    // hosted session's transcript comes from the daemon, so a rollout watcher would double-ingest it.
    Task RunPostStdoutWork(
            HookSpool spool, JsonNode? enrichedNode, string? sessionId, HookPostOutcome outcome, CodexHookEnvironment env) {
        var transcriptSpool = new TranscriptSpool(config, clock.Time, Url);

        _ = Poster(env).DrainSpoolsAsync(spool, transcriptSpool, sessionId);

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return Task.CompletedTask;

        var transcript = TryGetString(enrichedNode, "transcript_path");
        var cwd        = TryGetString(enrichedNode, "cwd");

        return sessionId is not null && transcript is not null && !env.HostedAppServer
            ? watchers.EnsureWatcherRunning(sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex", stderr: env.Stderr)
            : Task.CompletedTask;
    }

    async Task<int> HandleStop(JsonNode node, CodexHookEnvironment env) {
        // Codex 'Stop' fires at every turn end, NOT session end. Session-end
        // is fired by the watcher's parent-PID monitor in WatchCommand.cs
        // when the codex process actually exits — that path POSTs
        // /hooks/session-end/codex with reason: "parent_exited" and handles
        // generate_whats_done. Treating Stop as session-end here would kill
        // the watcher after turn 1 and mismark multi-turn sessions as ended
        // before they actually finish.
        //
        // We keep the watcher alive (in case it crashed mid-session) AND
        // best-effort POST /hooks/stop so the server can emit the idle-wait
        // marker that clears the chat "working" indicator (symmetric with
        // Claude's stop hook).
        var sessionId  = TryGetString(node, "session_id");
        var transcript = TryGetString(node, "transcript_path");
        var cwd        = TryGetString(node, "cwd");

        if (sessionId is not null && transcript is not null) {
            // The daemon owns an envelope-sourced hosted session's transcript; the idle-marker stop
            // POST still fires so the "working" indicator clears.
            if (!env.HostedAppServer) {
                await watchers.EnsureWatcherRunning(sessionId, transcript,
                    agentId: null, sessionIdOverride: null, cwd: cwd,
                    skipTitle: false, vendor: "codex", stderr: env.Stderr
                );
            }

            await PostBestEffortAsync("stop", node, TimeSpan.FromSeconds(2));
        }

        // Codex's stop-hook output parser rejects empty stdout as
        // "invalid stop hook JSON output". Emit the schema default explicitly.
        WriteSessionScopedOutput(env.Stdout);
        return 0;
    }

    /// <summary>Freezes the session's approval policy at its start, so an edit landing before its first tool call
    /// cannot change what governs it.</summary>
    void FreezePolicySnapshot(string sessionId, string? repoRoot) {
        try {
            new PolicySnapshotStore(config).LoadOrBuild(sessionId, repoRoot);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The first tool call builds it instead; the hook never fails over a snapshot.
        }
    }

    /// <summary>A deny rule binds a call Codex would run unasked, in every launch mode: a watched session, a
    /// daemon-hosted terminal, and the hosted app-server, where this hook also fires.</summary>
    async Task<int> HandlePreToolUse(JsonNode node, CodexHookEnvironment env) {
        if (node is JsonObject payload && TryGetString(node, "session_id") is { } sessionId)
            await new CodexPolicySeam(config, clock.Time, new HookSpool(config, clock.Time, Url)).HandlePreToolUseAsync(payload, sessionId, env.Stdout);
        return 0;
    }

    async Task<int> HandlePermissionRequest(JsonNode node, CodexHookEnvironment env) {
        // An envelope-sourced hosted session answers approvals over the app-server protocol itself:
        // Codex's requestApproval reaches the daemon's CodexApprovalBridge, which surfaces it to the
        // user and fails closed on its own deadline. Bouncing this hook to the daemon bridge would
        // answer first, through a channel with no deadline, and that decline would never come.
        if (env.HostedAppServer)
            return YieldToCodex(env);

        return hosted.Bridge switch {
            DaemonBridge.Loopback bridge => await HandlePermissionRequestViaBridge(bridge.BaseUrl, node, env),
            DaemonBridge.NotLoopback bad => RefuseBridge(env, bad.Value),
            _                            => await HandlePermissionRequestStub(node, env),
        };
    }

    /// <summary>Empty hookSpecificOutput — Codex reads it as "no decision" and runs its own approval
    /// flow (codex-rs/hooks/src/events/permission_request.rs).</summary>
    static int YieldToCodex(CodexHookEnvironment env) {
        env.Stdout.Write("{}");
        return 0;
    }

    /// A named bridge this hook may not post to is a misconfiguration, not a terminal session: the
    /// stub's silent fallback to Codex's own prompt would hide it, and this hook fails closed.
    static int RefuseBridge(CodexHookEnvironment env, string daemonUrl) {
        env.Stderr.WriteLine(
            $"[kcap] codex-hook permission-request: {HostedAgent.BridgeUrlVar} must be http loopback, got: {daemonUrl}");

        return EmitDenyAndExitNonzero(env);
    }

    async Task<int> HandlePermissionRequestStub(JsonNode node, CodexHookEnvironment env) {
        if (node is JsonObject payload && TryGetString(node, "session_id") is { } sessionId) {
            var budget = clock.Budget(Ceiling).Remaining;
            var seam   = new CodexPolicySeam(config, clock.Time, new HookSpool(config, clock.Time, Url), PolicyJudgeGateway.ForHook(http, Url, clock.Time));
            if (await seam.HandlePermissionRequestAsync(payload, sessionId, env.Stdout,
                    budget < CodexPolicySeam.JudgeBudget ? budget : CodexPolicySeam.JudgeBudget) == SeamAnswer.Answered)
                return 0;
        }

        // Terminal Codex sessions can't answer a Capacitor UI prompt, so we
        // record the event server-side (best-effort) and emit no decision —
        // Codex falls back to its built-in in-CLI approval flow and the user
        // answers there.
        //
        // Do NOT post to /hooks/permission-request/{vendor} — that route runs
        // RunPermissionFlow which long-polls up to 10 hours waiting for a
        // hosted-agent UI decision. With Codex's 30 s hook timeout, the hook
        // process is killed long before the server returns.
        //
        // Single 2 s deadline covers BOTH the /auth/config discovery and the
        // /hooks/permission-record POST inside PostBestEffortAsync.
        // Without bounding discovery too, a server that accepts the TCP
        // connection but stalls on /auth/config can burn the full HttpClient
        // default (100 s) before we even start the POST, blowing past Codex's
        // 30 s hook timeout.
        // Recording must never block Codex's approval prompt — see
        // PostBestEffortAsync for the shared swallow-all/cap behavior.
        await PostBestEffortAsync("permission-record", node, TimeSpan.FromSeconds(2));

        return YieldToCodex(env);
    }

    async Task<int> HandlePermissionRequestViaBridge(string bridgeBase, JsonNode node, CodexHookEnvironment env) {
        using var client = http.Loopback();
        // The daemon holds the request open until the human decides, and Codex sets no timeout of
        // its own on this hook, so the wait is theirs to end rather than the client's.
        client.Timeout = Timeout.InfiniteTimeSpan;

        try {
            var bridgePayload = BuildBridgePayload(node, hosted.AgentId);
            using var content = new StringContent(bridgePayload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostAsync($"{bridgeBase}/codex/permission-request", content);

            if (!resp.IsSuccessStatusCode) {
                env.Stderr.WriteLine(
                    $"[kcap] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
                return EmitDenyAndExitNonzero(env);
            }

            var body = await resp.Content.ReadAsStringAsync();
            env.Stdout.Write(body);
            return 0;
        } catch (Exception ex) {
            env.Stderr.WriteLine($"[kcap] codex-hook permission-request bridge error: {ex.Message}");
            return EmitDenyAndExitNonzero(env);
        }
    }

    internal static JsonObject BuildBridgePayload(JsonNode node, string? agentId) {
        var payload = (JsonObject)node.DeepClone();
        // The hook's agent_id is the subagent's; agent_id on this wire is the hosted agent.
        if (SubagentIdOf(node) is { } subagentId) payload["subagent_id"] = subagentId;
        payload.Remove("agent_id");
        if (agentId is not null) payload["agent_id"] = agentId;
        return payload;
    }

    static int EmitDenyAndExitNonzero(CodexHookEnvironment env) {
        var response = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = new JsonObject { ["behavior"] = "deny" }
            }
        };

        env.Stdout.Write(response.ToJsonString());
        return 1;
    }

    /// <summary>
    /// Best-effort POST of <paramref name="node"/> to <c>/hooks/{endpoint}</c>, capped at
    /// <paramref name="cap"/> and swallowing every failure — it must never block, throw, or
    /// terminate the caller. The single deadline covers both /auth/config discovery and the POST.
    /// Callers that must satisfy Codex's stdout contract write their JSON output AFTER awaiting this.
    /// </summary>
    async Task PostBestEffortAsync(string endpoint, JsonNode node, TimeSpan cap) {
        // Silently, and before the deadline is armed: this method owes the caller no output, and a
        // URL nothing can be sent to is not worth spending the cap discovering.
        if (!HookHttp.IsPostable(Url)) {
            return;
        }

        using var cts = new CancellationTokenSource(cap, clock.Time);

        try {
            // The hook verb, so a lapse writes nothing to stderr: a per-turn Stop would spam it, and
            // Codex reads hook stderr as the hook's own result.
            var (client, status) = await http.ForHookAsync(cts.Token);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                    return;
                }

                using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{Url}/hooks/{endpoint}", content, cts.Token);
            }
        } catch {
            // Best-effort — must never block or fail the caller.
        }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = TryGetString(node, fieldName);

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    /// <summary>
    /// Safely extracts a string from <paramref name="node"/>[<paramref name="fieldName"/>].
    /// Returns null (instead of throwing) when the field is absent, null, or not a string.
    /// </summary>
    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
