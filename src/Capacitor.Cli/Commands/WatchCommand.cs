using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;
using Capacitor.Cli.Core.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

static partial class WatchCommand {
    /// <summary>Outcome of deciding whether the parent-exit watchdog can run.</summary>
    internal enum ParentWatchdog {
        /// <summary>Parent PID is alive — start the 5s liveness poll.</summary>
        Monitor,

        /// <summary>No parent PID was supplied — nothing to monitor.</summary>
        NoParentPid,

        /// <summary>A parent PID was supplied but it's already dead at startup
        /// (typically a transient process from bad PID resolution). Must be surfaced,
        /// never silently skipped.</summary>
        ParentAlreadyDead
    }

    /// <summary>
    /// Decides whether the parent-exit watchdog should run. Pure so the three
    /// outcomes — including the dead-at-startup case that caused stuck sessions —
    /// are unit-testable without spawning processes.
    /// </summary>
    internal static ParentWatchdog DecideParentWatchdog(int? parentPid, Func<int, bool> isAlive) =>
        parentPid is not { } ppid ? ParentWatchdog.NoParentPid
        : !isAlive(ppid)          ? ParentWatchdog.ParentAlreadyDead
        :                           ParentWatchdog.Monitor;

    public static async Task<int> RunWatch(
            string  baseUrl,
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string? cwd,
            bool    skipTitle = false,
            int?    parentPid = null,
            string  vendor    = "claude"
        ) {
        // Redirect all output to a log file so we don't hold parent's pipe FDs open
        var logDir = PathHelpers.ConfigPath("logs");
        Directory.CreateDirectory(logDir);
        var logKey    = agentId is not null ? $"{sessionId}-{agentId}" : sessionId;
        var logPath   = Path.Combine(logDir, $"{logKey}.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        // Detach from controlling terminal so closing the parent's terminal does
        // not deliver SIGHUP to the watcher. Without this, both the coding agent
        // and the watcher die simultaneously when the user closes the terminal
        // window, and the parent-PID poll below never fires — leaving the
        // session stuck "active" because session-end is never POSTed.
        if (!ProcessHelpers.DetachFromControllingTerminal()) {
            Log("setsid() failed; SIGHUP from terminal close may kill the watcher");
        }

        using var cts = new CancellationTokenSource();

        // Tracks whether shutdown was triggered by the parent coding-agent process
        // dying without firing session-end. When true (1), the watcher takes over the
        // server's session-end POST (which the parent normally fires) so the read
        // model doesn't keep the session "active" forever.
        //
        // Written by the parent-PID monitor task and signal handlers, read on the
        // main thread — use Interlocked/Volatile so the C# memory model formally
        // guarantees the write is observed even though awaits already act as memory
        // barriers in practice.
        var parentExited = 0;

        // Handle SIGTERM/SIGINT for graceful shutdown.
        //
        // PosixSignalRegistration.Create returns an IDisposable that owns the
        // underlying handler slot; if it's not rooted for the lifetime of the
        // method the finalizer silently unregisters the handler. `using var`
        // keeps both registrations alive until RunWatch returns and disposes
        // them deterministically.
        //
        // For SIGTERM and SIGHUP, ctx.Cancel = true is required: .NET runs the
        // signal's default action (terminate) after the handler unless the
        // context is cancelled, which would kill the watcher before the main
        // loop notices cts and the drain / session-end POST never run.
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            cts.Cancel();
        };
        using var sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
            cts.Cancel();
            ctx.Cancel = true;
        });

        // SIGHUP defense-in-depth: setsid above should prevent the kernel from
        // delivering SIGHUP to us, but if a shell forwards SIGHUP explicitly to
        // its process group children before our setsid lands, or if setsid
        // failed, this handler keeps the watcher alive long enough to run the
        // parent-exit cleanup path (drain + session-end POST).
        using var sighupReg = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => {
            Log("Received SIGHUP; treating as parent-exit");
            Interlocked.Exchange(ref parentExited, 1);
            cts.Cancel();
            ctx.Cancel = true;
        });

        // Watch the spawning coding-agent process. If it dies without firing
        // session-end (crash, force-kill, IDE-detach), self-terminate within ~5s and
        // POST session-end instead of orphaning. Crucially, the cases where we DON'T
        // monitor are now logged: a silently-skipped watchdog is exactly the failure
        // that left sessions stuck "active" with the watcher still connected — the
        // resolved parent PID was already dead at startup and nothing recorded it.
        switch (DecideParentWatchdog(parentPid, ProcessHelpers.IsProcessAlive)) {
            case ParentWatchdog.NoParentPid:
                Log("No parent pid supplied; parent-exit watchdog disabled (session-end relies on the agent's own hook)");

                break;

            case ParentWatchdog.ParentAlreadyDead:
                Log($"Parent pid {parentPid} already dead at watcher startup; parent-exit watchdog NOT started — "
                  + "session-end will not be POSTed if the agent ends abruptly. This usually means parent-PID "
                  + "resolution returned a transient process; see ProcessHelpers.GetCodingAgentPid.");

                break;

            case ParentWatchdog.Monitor:
                var ppid = parentPid!.Value;
                Log($"Monitoring parent pid {ppid}");
                _ = Task.Run(async () => {
                    while (!cts.Token.IsCancellationRequested) {
                        try {
                            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                        } catch (OperationCanceledException) {
                            return;
                        }

                        if (!ProcessHelpers.IsProcessAlive(ppid)) {
                            Log($"Parent pid {ppid} exited; shutting down watcher");
                            Interlocked.Exchange(ref parentExited, 1);
                            cts.Cancel();

                            return;
                        }
                    }
                }, cts.Token);

                break;
        }

        var state = new WatchState();
        state.LastActivityAt = DateTimeOffset.UtcNow;
        // Antigravity posts /hooks/session-start BEFORE the watcher spawns, so the session is
        // already committed server-side — the below-threshold buffering (which exists to avoid
        // junk sessions the server hasn't seen) doesn't apply. Treat it as past-threshold from
        // the start so short conversations stream live and still idle-end + post session-end
        // (otherwise a <10-line conversation would never reach threshold, never idle-end, and
        // leave the session Active with the watcher lingering — the IDE process outlives it).
        if (vendor == "antigravity") state.ThresholdReached = true;
        // Antigravity is a GUI app like the Codex desktop: its shared process never
        // exits per-conversation, so (like Codex) the idle timeout — not a parent-exit
        // watchdog — is the per-conversation session-end path. Its own knob so tenants
        // can tune the two GUIs independently.
        var idleTimeout = vendor == "antigravity"
            ? ResolveCodexIdleTimeout(Environment.GetEnvironmentVariable("KCAP_ANTIGRAVITY_IDLE_MINUTES"))
            : ResolveCodexIdleTimeout(Environment.GetEnvironmentVariable("KCAP_CODEX_IDLE_MINUTES"));
        var idleExit = false;

        if (skipTitle) {
            state.TitleGenerated = true;
        }

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
            state.LastRepoDetection = DateTimeOffset.UtcNow;
        }

        Log($"Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        // Build SignalR hub connection
        var hubUrl = $"{baseUrl}/hubs/sessions";

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options => {
                    options.AccessTokenProvider = async () => {
                        var t = await TokenStore.GetValidTokensAsync();

                        return t?.AccessToken;
                    };
                }
            )
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, CapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Halve KeepAliveInterval (15s → 7s); see ServerConnection for rationale.
        // ServerTimeout stays at the 30s default for rollout safety.
        hubConnection.KeepAliveInterval = TimeSpan.FromSeconds(7);

        // Register StopWatcher handler — server sends this to tell us to shut down
        hubConnection.On<string>(
            "StopWatcher",
            reason => {
                Log($"Received StopWatcher signal: {reason}");
                cts.Cancel();
            }
        );

        hubConnection.Reconnecting += ex => {
            Log($"SignalR reconnecting: {ex?.Message}");

            return Task.CompletedTask;
        };

        hubConnection.Reconnected += async connectionId => {
            Log($"SignalR reconnected: {connectionId}");

            // Re-register with server and check if it's behind us (gap recovery)
            try {
                var serverPosition = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cancellationToken: cts.Token);

                if (serverPosition < state.LinesProcessed) {
                    Log($"Server behind ({serverPosition} vs {state.LinesProcessed}), rewinding to resend gap");
                    state.LinesProcessed = serverPosition;
                }
            } catch (Exception ex) {
                Log($"Re-register after reconnect failed: {ex.Message}");
            }
        };

        hubConnection.Closed += ex => {
            Log($"SignalR connection closed: {ex?.Message}");
            cts.Cancel();

            return Task.CompletedTask;
        };

        // Connect with retry — server may not be up yet.
        // Retry indefinitely with exponential backoff (cap 30s).
        // The watcher is lightweight when idle and the session will end via session-end hook.
        var connectRetryDelay = TimeSpan.FromSeconds(1);

        while (!cts.Token.IsCancellationRequested) {
            try {
                await hubConnection.StartAsync(cts.Token);

                break;
            } catch (OperationCanceledException) {
                // SIGTERM/SIGINT during connect — exit gracefully
                break;
            } catch (Exception ex) {
                Log($"SignalR connect failed, retrying in {connectRetryDelay.TotalSeconds}s: {ex.Message}");

                try {
                    await Task.Delay(connectRetryDelay, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }

                connectRetryDelay = TimeSpan.FromSeconds(Math.Min(connectRetryDelay.TotalSeconds * 2, 30));
            }
        }

        if (cts.Token.IsCancellationRequested) {
            await hubConnection.DisposeAsync();
            await logWriter.DisposeAsync();

            return 0;
        }

        // Register with server and get resume position
        state.LinesProcessed = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cts.Token);
        Log($"Connected via SignalR, resuming from line {state.LinesProcessed}");

        // Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
        // transcripts itself and spawns a child watcher per subagent (AI-900). Tracks the
        // files already registered + spawned so each is handled exactly once across ticks.
        var seenSubagents = new HashSet<string>(StringComparer.Ordinal);

        try {
            while (!cts.Token.IsCancellationRequested) {
                // Skip work while disconnected — SignalR auto-reconnect handles recovery.
                // No point re-reading the file or attempting sends that will fail.
                if (hubConnection.State != HubConnectionState.Connected) {
                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    } catch (OperationCanceledException) {
                        break;
                    }

                    continue;
                }

                // Periodically refresh repository info (every 60s)
                if (cwd is not null && DateTimeOffset.UtcNow - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
                    state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                }

                await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, vendor, cts.Token);

                // Live subagent discovery: only the parent (agentId == null) watcher scans;
                // child subagent watchers (agentId != null) just stream their file. Gemini
                // scans its native nested chat files; OpenCode scans the nested dir the
                // kcap plugin writes child {info,parts} into (AI-919 phase 2).
                if (agentId is null && vendor == "gemini") {
                    await ScanGeminiSubagents(baseUrl, sessionId, transcriptPath, seenSubagents, cts.Token);
                } else if (agentId is null && vendor == "opencode") {
                    await ScanOpenCodeSubagents(baseUrl, sessionId, transcriptPath, seenSubagents, cts.Token);
                }

                if (ShouldEndOnIdle(
                        vendor,
                        isSessionWatcher: agentId is null,
                        state.ThresholdReached,
                        state.LastActivityAt,
                        DateTimeOffset.UtcNow,
                        idleTimeout,
                        // A tool awaiting its result suppresses idle-end: Codex tracks call_ids,
                        // Antigravity counts PLANNER_RESPONSE calls vs result steps (AI-1157 review).
                        toolInFlight: vendor == "antigravity"
                            ? state.PendingAntigravityToolCalls > 0
                            : state.PendingCodexToolCalls.Count > 0)) {
                    Log($"{vendor} transcript idle for >{idleTimeout.TotalMinutes:F0}m; ending session (idle_timeout)");
                    idleExit = true;
                    cts.Cancel();

                    break;
                }

                try {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } catch (OperationCanceledException) {
            // Expected
        }

        // Final drain before exit
        if (agentId is null && !state.ThresholdReached) {
            // Session watcher never reached threshold — short-lived session.
            // Skip sending buffered lines so the server can clean up the empty session.
            Log($"Session below threshold ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} lines), skipping final drain");
        } else {
            Log("Draining remaining lines...");
            await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, vendor, CancellationToken.None, isFinalDrain: true);
        }

        // Signal drain complete to server.
        // cts.Token is already cancelled by the time we reach here (that's the path
        // that exits the main loop), so passing it would throw OperationCanceledException
        // before the call lands. Use a fresh short-lived token so the server actually
        // hears the drain-complete signal and can release StopAndDrainAsync waiters.
        try {
            if (hubConnection.State == HubConnectionState.Connected) {
                using var drainCompleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await hubConnection.InvokeAsync("WatcherDrainComplete", sessionId, agentId, cancellationToken: drainCompleteCts.Token);
                Log("Drain complete signaled to server");
            }
        } catch (Exception ex) {
            Log($"Failed to signal drain complete: {ex.Message}");
        }

        Log($"Done. {state.LinesProcessed} total lines processed.");

        await hubConnection.DisposeAsync();

        // Parent coding-agent died without firing session-end. Take over the role:
        // POST /hooks/session-end so the server writes SessionEnded (otherwise the
        // session stays "active" forever in the read model). Mirrors what the daemon
        // already does for hosted agents via EndAgentSessionAsync. Skipped for:
        //   - agent watchers (agentId != null) — their lifecycle is handled by the
        //     parent session's own session-end hook plus the daemon path.
        //   - below-threshold sessions — no transcript lines were ever sent, so the
        //     server may not have a meaningful session to end.
        // Runs after SignalR dispose so the server's StopAndDrainAsync skips the
        // 10s drain wait (no live watcher connection to signal).
        var endReason = Volatile.Read(ref parentExited) == 1 ? "parent_exited"
                      : idleExit                              ? "idle_timeout"
                      :                                         null;

        if (endReason is not null && agentId is null && state.ThresholdReached) {
            await PostSessionEndOnParentExitAsync(baseUrl, sessionId, transcriptPath, cwd, vendor, state.Repository, endReason);
        }

        await logWriter.DisposeAsync();

        return 0;
    }

    /// <summary>
    /// Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
    /// transcripts itself (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>). On first
    /// sight of a file it registers the subagent (<c>subagent-start</c>, fail-closed) then
    /// spawns a detached child watcher that streams it with the subagent's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <paramref name="seen"/>;
    /// deterministic server-side lifecycle ids make re-registration safe. AI-900.
    /// </summary>
    static async Task ScanGeminiSubagents(
            string          baseUrl,
            string          sessionId,
            string          transcriptPath,
            HashSet<string> seen,
            CancellationToken ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = GeminiSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        IReadOnlyDictionary<string, string>? types = null;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var subId = Path.GetFileNameWithoutExtension(subFile);
            if (!Guid.TryParse(subId, out _)) continue; // not a <subId>.jsonl

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(subId);
            types       ??= GeminiSubagentDiscovery.ResolveAgentTypes(transcriptPath);
            var agentType = types.GetValueOrDefault(subId) ?? "subagent";

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostSubagentStartAsync(baseUrl, sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await WatcherManager.EnsureWatcherRunning(
                baseUrl, key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "gemini");

            Log($"Gemini subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    static async Task<bool> PostSubagentStartAsync(
        string baseUrl, string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, ct);
            var       payload = GeminiSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-start", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// OpenCode fires no subagent hooks; its subagents live in its SQLite db, so the kcap
    /// plugin fetches each child session via the SDK and writes its <c>{info,parts}</c> JSONL
    /// into <c>&lt;cacheDir&gt;/&lt;parentSid&gt;/&lt;childSid&gt;.jsonl</c>. The parent watcher discovers
    /// those files (mirroring <see cref="ScanGeminiSubagents"/>): on first sight it registers
    /// the subagent (<c>subagent-start</c>, fail-closed) then spawns a detached child watcher
    /// that streams it with the canonical agentId (= childSid) → <c>AgentSubsession-*</c>, which
    /// lines up with the agentId the server surfaced from the parent's <c>task</c> tool call.
    /// Idempotent across ticks via <paramref name="seen"/>; deterministic server-side lifecycle
    /// ids make re-registration safe. AI-919 phase 2.
    /// </summary>
    static async Task ScanOpenCodeSubagents(
            string            baseUrl,
            string            sessionId,
            string            transcriptPath,
            HashSet<string>   seen,
            CancellationToken ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostOpenCodeSubagentStartAsync(baseUrl, sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await WatcherManager.EnsureWatcherRunning(
                baseUrl, key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "opencode");

            Log($"OpenCode subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    static async Task<bool> PostOpenCodeSubagentStartAsync(
        string baseUrl, string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, ct);
            var       payload = OpenCodeSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-start", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Whitelist of vendor values accepted by the server's session-end route.
    /// Used to reject unexpected --vendor input before interpolating into the URL
    /// path (defence-in-depth against path traversal even though the CLI runs locally).
    /// </summary>
    static readonly HashSet<string> KnownVendors = new(StringComparer.Ordinal) { "claude", "codex", "copilot", "gemini", "kiro", "pi", "opencode", "antigravity" };

    /// <summary>
    /// Total time budget for the parent-exit session-end POST. Covers /auth/config
    /// discovery + retrying POST. Short by design — this runs on the watcher's
    /// shutdown path; a stalled server must not block process termination, the whole
    /// point of the parent-PID watchdog is to self-terminate within ~5s.
    /// </summary>
    static readonly TimeSpan ParentExitPostBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Codex has no session-end hook and the desktop app's single long-lived
    /// `codex app-server` process is the watchdog's parent PID for EVERY
    /// conversation, so the parent-exit watchdog never fires per-conversation —
    /// sessions stay "active" until the whole app quits. This idle window is the
    /// fallback: if the rollout file gets no new lines for this long, the watcher
    /// POSTs session-end (reason "idle_timeout"). Self-correcting — Codex fires a
    /// Stop hook per turn that re-spawns the watcher and the read model
    /// reactivates the session, so a resumed conversation comes back to life.
    /// </summary>
    internal static readonly TimeSpan DefaultCodexIdleTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Resolves the Codex idle timeout from KCAP_CODEX_IDLE_MINUTES, falling back
    /// to <see cref="DefaultCodexIdleTimeout"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveCodexIdleTimeout(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCodexIdleTimeout;

    /// <summary>
    /// Whether the watcher should self-terminate and POST session-end because the
    /// vendor's transcript file has gone idle. Pure so the policy is unit-testable.
    /// Gated to: the two GUI vendors whose parent-exit watchdog can't fire
    /// per-conversation — codex (the desktop app's shared app-server never exits per
    /// session) and antigravity (the IDE process outlives any one conversation) —
    /// session watchers (not subagents), and threshold-reached sessions
    /// (below-threshold short-lived sessions have no server session to end). Uses
    /// strictly-greater-than so the boundary tick is not yet considered idle.
    /// Also suppressed when a tool call is in flight (<paramref name="toolInFlight"/>
    /// true): a long-running shell command / custom tool legitimately produces no
    /// new rollout lines between its function_call start and its _output completion —
    /// ending while it's running would falsely terminate an active session. No hard
    /// ceiling on tool duration: if the process dies, the parent-exit watchdog takes
    /// over; a hung-but-alive tool is a real in-flight turn (YAGNI — no ceiling).
    /// </summary>
    internal static bool ShouldEndOnIdle(
            string         vendor,
            bool           isSessionWatcher,
            bool           thresholdReached,
            DateTimeOffset lastActivityAt,
            DateTimeOffset now,
            TimeSpan       idleTimeout,
            bool           toolInFlight = false
        ) =>
        (vendor == "codex" || vendor == "antigravity")
        && isSessionWatcher
        && thresholdReached
        && now - lastActivityAt > idleTimeout
        && !toolInFlight;

    /// <summary>
    /// Updates <paramref name="pending"/> based on a single Codex rollout line.
    /// A <c>function_call</c> or <c>custom_tool_call</c> response_item adds its
    /// <c>call_id</c> to the set; the matching <c>_output</c> variant removes it.
    /// All other lines and malformed JSON are silently ignored so this is safe to
    /// call unconditionally for every line of any vendor transcript.
    /// </summary>
    internal static void UpdateCodexPendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return;

            var payload = root.Obj("payload");

            if (payload is not { } p) return;

            var callId = p.Str("call_id");

            if (callId is null) return;

            switch (p.Str("type")) {
                case "function_call"
                  or "custom_tool_call":
                    pending.Add(callId);
                    break;

                case "function_call_output"
                  or "custom_tool_call_output":
                    pending.Remove(callId);
                    break;
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    internal static async Task PostSessionEndOnParentExitAsync(
            string             baseUrl,
            string             sessionId,
            string             transcriptPath,
            string?            cwd,
            string             vendor,
            RepositoryPayload? repository,
            string             reason = "parent_exited"
        ) {
        if (!KnownVendors.Contains(vendor)) {
            Log($"Parent-exit session-end skipped: unknown vendor '{vendor}'");
            return;
        }

        // Gemini fires no subagent-stop hook and the child watchers spawned in ScanGeminiSubagents
        // carry no parent-pid watchdog, so when the parent process dies WITHOUT the session-end
        // hook this is the only place that finalizes live subagents. Mirror the hook path: kill
        // each child watcher, drain its tail, POST subagent-stop — capped so a slow drain can't
        // block the watchdog's self-termination, and run BEFORE the session-end POST so
        // SubagentCompleted lands ahead of SessionEnded. No-op when none were spawned (AI-900).
        if (vendor == "gemini") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => GeminiSubagentTeardown.DrainAsync(baseUrl, sessionId, transcriptPath),
                    GeminiSubagentTeardown.DrainCap);

                if (!finalized) {
                    Log("Parent-exit Gemini subagent teardown cap elapsed; "
                      + "unfinalized subagents recover via: kcap import --gemini");
                }
            } catch (Exception ex) {
                Log($"Parent-exit Gemini subagent teardown failed: {ex.Message}");
            }
        }

        // OpenCode likewise fires no subagent-stop hook (the plugin-written child files are
        // discovered + streamed by ScanOpenCodeSubagents, with no parent-pid watchdog on the
        // child watchers), so the parent exit is the only place that finalizes them. Same
        // shape as the Gemini teardown; runs BEFORE the session-end POST so SubagentCompleted
        // lands ahead of SessionEnded. No-op when none were spawned (AI-919 phase 2).
        if (vendor == "opencode") {
            // DrainAsync is self-bounding (per-step caps + a shared cleanup deadline + a hard
            // overall ceiling), so it needs no outer time cap here — wrapping it in one risked
            // cutting later children's SubagentCompleted. It attempts subagent-stop for every child
            // WITHIN the overall budget; under a pathological/huge child count it stops at the
            // ceiling and returns how many were left unfinalized (logged below — OpenCode has no
            // historical import to recover a missed stop).
            try {
                var unfinalized = await OpenCodeSubagentTeardown.DrainAsync(baseUrl, sessionId, transcriptPath);
                if (unfinalized > 0) {
                    Log($"Parent-exit OpenCode subagent teardown hit the {OpenCodeSubagentTeardown.OverallBudget.TotalSeconds:0}s ceiling; "
                      + $"{unfinalized} subagent(s) left without SubagentCompleted");
                }
            } catch (Exception ex) {
                Log($"Parent-exit OpenCode subagent teardown failed: {ex.Message}");
            }
        }

        using var budgetCts = new CancellationTokenSource(ParentExitPostBudget);

        try {
            var endHook = new JsonObject {
                ["session_id"]      = sessionId,
                ["transcript_path"] = transcriptPath,
                ["cwd"]             = cwd ?? "",
                ["hook_event_name"] = "session_end",
                ["reason"]          = reason,
                // Stamp the exit time so the server can scope the SessionEnded id
                // per run. Vendors with no session-end hook (Kiro) end ONLY via this
                // path, so without a timestamp a resumed session's second exit would
                // dedupe against the first and the session would stay Active.
                // Computed once here and reused across POST retries, so it stays
                // idempotent for a single exit.
                ["ended_at"]        = DateTimeOffset.UtcNow.ToString("O"),
            };

            if (repository is not null) {
                endHook["repository"] = JsonNode.Parse(
                    JsonSerializer.Serialize(repository, CapacitorJsonContext.Default.RepositoryPayload)
                );
            }

            using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, budgetCts.Token);
            using var content    = new StringContent(endHook.ToJsonString(), Encoding.UTF8, "application/json");

            var url = $"{baseUrl}/hooks/session-end/{vendor}";
            using var response = await httpClient.PostWithRetryAsync(url, content, timeout: ParentExitPostBudget, ct: budgetCts.Token);

            if (!response.IsSuccessStatusCode) {
                Log($"Parent-exit session-end POST returned HTTP {(int)response.StatusCode}");
                return;
            }

            Log("Parent-exit session-end POST succeeded");

            try {
                var body = await response.Content.ReadAsStringAsync(budgetCts.Token);
                var node = JsonNode.Parse(body);

                if (node?["generate_whats_done"]?.GetValue<bool>() == true) {
                    WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId, vendor);
                }
            } catch (Exception ex) {
                Log($"Parent-exit session-end response parse failed: {ex.Message}");
            }
        } catch (OperationCanceledException) {
            Log($"Parent-exit session-end POST timed out after {ParentExitPostBudget.TotalSeconds:F0}s");
        } catch (Exception ex) {
            Log($"Parent-exit session-end POST failed: {ex.Message}");
        }
    }

    static readonly Regex CommandNameRegex = CommandNameRx();
    static          bool  parseErrorLogged;

    static async Task DrainNewLines(
            HubConnection     hubConnection,
            string            sessionId,
            string            transcriptPath,
            string?           agentId,
            WatchState        state,
            string            vendor,
            CancellationToken ct,
            bool              isFinalDrain = false
        ) {
        try {
            if (!File.Exists(transcriptPath)) {
                return;
            }

            // Read only newline-TERMINATED lines. A final line still being written by the agent
            // (no trailing '\n' yet) is held back — sending its truncated prefix and advancing the
            // position past it permanently drops the completed line (AI-1243: dropped Read results;
            // large tool_result lines are slow to flush and get caught mid-write). The next drain
            // re-reads it once complete.
            NewTranscriptLines drainRead;
            await using (var stream = new FileStream(
                    transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                drainRead = await ReadNewCompleteLinesAsync(
                    stream, state.LinesProcessed, holdIncompleteFinalLine: !isFinalDrain, ct);
            }

            var newLineNumbers = drainRead.LineNumbers;
            var newLines       = drainRead.Lines.Select(SecretRedactor.RedactLine).ToList();
            var linesRead      = drainRead.NextPosition;

            // Track Antigravity in-flight tool calls + the latest step timestamp from this
            // drain's transcript lines (BEFORE appending USAGE lines, which aren't transcript
            // steps). The created_at anchors USAGE recency; the pending-call count suppresses a
            // premature idle session-end while a long command runs (no line between call/result).
            if (vendor == "antigravity") {
                foreach (var l in newLines) {
                    UpdateAntigravityPendingToolCalls(state, l);
                    if (TryGetAntigravityCreatedAt(l) is { } ca) state.LastAntigravityCreatedAt = ca;
                }
            }

            // Antigravity keeps per-generation tokens/model in the sibling conversation .db
            // (derived from the transcript path — the session id here is the canonical dashless
            // form, but the transcript path carries the real conversation id), not the JSONL;
            // poll for newly-appended gen_metadata rows and stream them as synthetic USAGE
            // lines (server → AntigravityUsageBackfilledEvent). Only outside the below-threshold
            // buffering phase (a session watcher that hasn't reached threshold buffers without
            // sending — injecting there would either dupe on re-read or be lost on a flush
            // failure); subagent watchers (agentId != null) never buffer. The gen watermark
            // advances only after a successful send (below), so a failed batch re-reads the rows.
            var antigravityGenMax = -1L;
            if (vendor == "antigravity" && (agentId is not null || state.ThresholdReached)) {
                antigravityGenMax = AppendAntigravityUsageLines(state, newLines, newLineNumbers, transcriptPath, state.LastAntigravityCreatedAt);
            }

            if (newLines.Count > 0) {
                state.LastActivityAt = DateTimeOffset.UtcNow;
            }

            // Track Codex tool calls in flight across all new lines (Codex-only,
            // but runs unconditionally so it is not gated on the title phase or
            // threshold). Non-Codex lines have a different top-level shape (no
            // response_item), so this is a cheap no-op for them.
            foreach (var line in newLines) {
                UpdateCodexPendingToolCalls(state.PendingCodexToolCalls, line);
            }

            // Capture first user text (needed for title generation)
            if (state is { TitleGenerated: false, FirstUserText: null } && agentId is null) {
                // If we resumed from a later position, scan from the beginning of the file
                if (state is { LinesProcessed: > 0, FullFileScanDone: false }) {
                    Log("Scanning full file for first user text (resumed from later position)");

                    try {
                        await using var scanStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var       scanReader = new StreamReader(scanStream);

                        while (await scanReader.ReadLineAsync(ct) is { } scanLine) {
                            if (string.IsNullOrWhiteSpace(scanLine)) {
                                continue;
                            }

                            var userText = TryExtractUserText(scanLine, vendor);

                            if (userText is null) {
                                continue;
                            }

                            SetFirstUserText(state, userText);

                            if (state.FirstUserText is not null) {
                                break;
                            }
                        }

                        state.FullFileScanDone = true;
                    } catch (Exception ex) {
                        Log($"Full file scan for user text failed: {ex.Message}");
                    }
                }

                // Also check new lines (normal path)
                if (state.FirstUserText is null && newLines.Count > 0) {
                    foreach (var userText in newLines.Select(line => TryExtractUserText(line, vendor)).OfType<string>()) {
                        SetFirstUserText(state, userText);

                        if (state.FirstUserText is not null) {
                            break;
                        }
                    }
                }

                if (state.FirstUserText is not null) {
                    Log($"First user text captured ({state.FirstUserText.Length} chars)");
                }

                // Send truncated user text as the initial title immediately
                // (deferred until threshold is reached for session watchers)
                if (state is { InitialTitleSent: false, FirstUserText: not null, ThresholdReached: true } && agentId is null) {
                    state.InitialTitleSent = true;
                    _                      = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                }
            }

            // Count events and capture assistant context for LLM title generation
            if (state is { TitleGenerated: false } && agentId is null && newLines.Count > 0) {
                foreach (var line in newLines) {
                    if (IsEvent(line, vendor)) {
                        state.EventCount++;
                    }

                    if (state.FirstAssistantText is null) {
                        var assistantText = TryExtractAssistantText(line, vendor);

                        if (assistantText is not null) {
                            state.FirstAssistantText = assistantText.Length > 300 ? assistantText[..300] : assistantText;
                            Log($"First assistant text captured ({state.FirstAssistantText.Length} chars)");
                        }
                    }
                }
            }

            // Generate LLM title after enough events have accumulated
            // (deferred until threshold is reached for session watchers)
            if (state is { TitleGenerated: false, TitleInFlight: false, TitleAttempts: < 5, ThresholdReached: true }
             && agentId is null
             && state.FirstUserText is not null
             && state.EventCount >= 5) {
                Log($"Triggering LLM title generation (attempt {state.TitleAttempts + 1}/5, events: {state.EventCount})");
                state.TitleInFlight = true;
                state.TitleAttempts++;
                _ = GenerateTitleAsync(hubConnection, sessionId, state, vendor);
            }

            switch (agentId) {
                // ── Buffering for session watchers ──────────────────────────────
                // Hold lines until threshold is reached to avoid polluting the server
                // with short-lived sessions (e.g. <local-command-caveat> prompts).
                // Subagent watchers (agentId != null) skip buffering entirely.
                case null when !state.ThresholdReached && newLines.Count > 0: {
                    state.BufferedLines.AddRange(newLines);
                    state.BufferedLineNumbers.AddRange(newLineNumbers);
                    state.LinesReadAhead = linesRead;

                    if (state.BufferedLines.Count < WatchState.TranscriptThreshold) {
                        Log($"Buffering {newLines.Count} line(s) ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} threshold)");

                        return;
                    }

                    // Threshold reached — flush the entire buffer
                    Log($"Threshold reached ({state.BufferedLines.Count} lines), flushing buffer");
                    state.ThresholdReached = true;
                    newLines               = [..state.BufferedLines];
                    newLineNumbers         = [..state.BufferedLineNumbers];
                    linesRead              = state.LinesReadAhead;
                    state.BufferedLines.Clear();
                    state.BufferedLineNumbers.Clear();

                    // Send the initial title now that we're flushing
                    if (state is { InitialTitleSent: false, FirstUserText: not null }) {
                        state.InitialTitleSent = true;
                        _                      = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                    }

                    break;
                }
                case null when !state.ThresholdReached:
                    // No new content lines while buffering — track file position
                    state.LinesReadAhead = linesRead;

                    return;
            }

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            if (newLines.Count == 0 && repoToSend is null) {
                // No content lines and no repo changes — safe to advance past blank/whitespace lines
                state.LinesProcessed = linesRead;

                return;
            }

            try {
                // SendTranscriptBatch takes a single TranscriptBatch record (arity 1) —
                // SignalR matches on argument COUNT and does NOT auto-supply C# optional
                // defaults (PR #576 / v0.4.0 incident), so a parameter object keeps the
                // contract stable: adding a field stays backward-compatible (this client
                // omits a null vendor; servers ignore unknown fields). This calls the
                // record-based `SendTranscriptBatch2` added in AI-850 (the legacy
                // positional `SendTranscriptBatch` stays on the server for older CLIs),
                // so it requires a server deployed with that method — server-before-CLI.
                await hubConnection.InvokeAsync(
                    "SendTranscriptBatch2",
                    new TranscriptBatch {
                        SessionId   = sessionId,
                        AgentId     = agentId,
                        Lines       = newLines.ToArray(),
                        LineNumbers = newLineNumbers.ToArray(),
                        Repository  = repoToSend,
                        Vendor      = vendor,
                    },
                    ct
                );

                if (newLines.Count > 0) {
                    Log($"Sent {newLines.Count} line(s) via SignalR");
                }

                if (repoToSend is not null) {
                    Log("Sent updated repository info via SignalR");
                }

                // Only advance position after successful send — if send fails,
                // the next drain cycle will re-read and resend the same lines.
                // KurrentDB event IDs are deterministic (from transcript UUIDs),
                // so re-sending is idempotent.
                state.LinesProcessed = linesRead;

                // Commit the Antigravity gen_metadata watermark ONLY now (after the batch
                // carrying its USAGE lines landed); a failed send above leaves it unchanged
                // so the rows re-read next drain instead of being skipped forever.
                if (antigravityGenMax > state.LastAntigravityGenIdx)
                    state.LastAntigravityGenIdx = antigravityGenMax;

                if (repoToSend is not null) {
                    state.LastSentRepository = repoToSend;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                if (newLines.Count > 0) {
                    Log($"SendTranscriptBatch failed, will retry from line {state.LinesProcessed}: {ex.Message}");
                } else {
                    // Repo-only batch failed — no transcript lines at risk, so advance
                    // position and defer retry to the next 60s repo detection cycle
                    state.LinesProcessed    = linesRead;
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                    Log($"Repo info send failed, will retry in 60s: {ex.Message}");
                }
            }
        } catch (IOException ex) {
            Log($"Error reading file: {ex.Message}");
        } catch (OperationCanceledException) {
            // Expected during shutdown
        }
    }

    static void SetFirstUserText(WatchState state, string userText) {
        var cmdMatch = CommandNameRegex.Match(userText);

        if (cmdMatch.Success) {
            // Skip slash commands — wait for the next real user prompt to generate the title
            Log($"Skipping slash command /{cmdMatch.Groups[1].Value} for title generation");

            return;
        }

        state.FirstUserText = userText;
    }

    internal static string? TryExtractAssistantText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"    => TryExtractCodexAssistantText(line),
            "copilot"  => TryExtractCopilotAssistantText(line),
            "kiro"     => TryExtractKiroAssistantText(line),
            "pi"       => TryExtractPiAssistantText(line),
            "opencode" => TryExtractOpenCodeText(line, "assistant"),
            "antigravity" => TryExtractAntigravityText(line, "assistant"),
            _          => TryExtractClaudeAssistantText(line)
        };

    static string? TryExtractClaudeAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant") {
                return null;
            }

            if (root.Obj("message")?.Arr("content") is not { } content) {
                return null;
            }

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") == "text") {
                    var text = block.Str("text")?.Trim();

                    if (!string.IsNullOrEmpty(text)) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractCodexAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "assistant") return null;

            return TitleGenerator.ExtractCodexBlockText(payload.Value, "output_text")?.Trim() is { Length: > 0 } text ? text : null;
        } catch {
            return null;
        }
    }

    internal static bool IsEvent(string line, string vendor = "claude") {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (vendor == "copilot") {
                // user.message / assistant.message are the only conversational
                // envelopes; everything else (hook.*, system.*, tool.*,
                // session.*) is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("type") is "user.message" or "assistant.message";
            }

            if (vendor == "kiro") {
                // Prompt / AssistantMessage are the conversational lines;
                // ToolResults is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("kind") is "Prompt" or "AssistantMessage";
            }

            if (vendor == "codex") {
                // Codex rolls everything into a top-level response_item envelope;
                // a "message" payload (user or assistant) is the analog of Claude's
                // user/assistant event for title-threshold purposes. User-role
                // payloads that are codex-injected preludes
                // (<environment_context>, AGENTS.md, <turn_aborted>) must NOT
                // count — otherwise a fresh session with no real prompts can
                // reach the 5-event threshold and produce a low-context title.
                if (root.Str("type") != "response_item") return false;

                var payload = root.Obj("payload");

                if (payload?.Str("type") != "message") return false;

                var role = payload.Value.Str("role");

                return role switch {
                    "assistant" => true,
                    "user"      => TitleGenerator.ExtractCodexBlockText(payload.Value, "input_text") is { } text
                                && !TitleGenerator.IsCodexInjectedUserPrelude(text),
                    _           => false
                };
            }

            if (vendor == "pi") {
                // Pi conversational envelopes are type:"message" with
                // message.role user/assistant (Pi has no top-level user/assistant
                // type). Gate on CONTENT, not just role: an empty user/assistant
                // envelope produces no canonical event (the server's NormalizeUser
                // returns null for empty content, NormalizeAssistant for zero
                // parts), so it must not count toward the title-event threshold
                // either. Mirrors PiImportSource.IsImportRelevantLine.
                if (root.Str("type") != "message") return false;
                if (root.Obj("message") is not { } piMsg) return false;

                return piMsg.Str("role") switch {
                    "user"      => PiContent.HasUserContent(piMsg),
                    "assistant" => PiContent.HasAssistantContent(piMsg),
                    _           => false
                };
            }

            if (vendor == "opencode") {
                // OpenCode envelopes are {info,parts} with info.role user/assistant.
                // Gate on text content: a turn with no non-hidden text (e.g. tool-only)
                // yields no titleable text, so it must not count toward the title-event
                // threshold. Mirrors the Pi content gate.
                if (root.Obj("info")?.Str("role") is not ("user" or "assistant")) return false;

                return OpenCodeTextParts(root.Arr("parts")) is not null;
            }

            if (vendor == "antigravity") {
                // Antigravity transcript_full.jsonl lines carry a `type`; only the two
                // conversational steps count toward the title-event threshold, and only
                // when they have text (a tool-only PLANNER_RESPONSE or an empty prompt
                // yields no titleable text). Everything else (RUN_COMMAND, VIEW_FILE,
                // GENERIC, CHECKPOINT, INVOKE_SUBAGENT, SYSTEM_*) is plumbing.
                return root.Str("type") switch {
                    "USER_INPUT"       => TryExtractAntigravityText(line, "user")      is not null,
                    "PLANNER_RESPONSE" => TryExtractAntigravityText(line, "assistant") is not null,
                    _                  => false
                };
            }

            return root.Str("type") is "user" or "assistant";
        } catch {
            return false;
        }
    }

    internal static string? TryExtractUserText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"    => TryExtractCodexUserText(line),
            "copilot"  => TryExtractCopilotUserText(line),
            "kiro"     => TryExtractKiroUserText(line),
            "pi"       => TryExtractPiUserText(line),
            "opencode" => TryExtractOpenCodeText(line, "user"),
            "antigravity" => TryExtractAntigravityText(line, "user"),
            _          => TryExtractClaudeUserText(line)
        };

    // ── OpenCode extractors (AI-919) ───────────────────────────────────────────
    //
    // OpenCode transcript lines are {info,parts} with info.role user/assistant. Title
    // text is the joined non-hidden text parts — mirrors the server's
    // OpenCodeTranscriptNormalizer.ExtractText so the watcher's titles match ingest.
    static string? TryExtractOpenCodeText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("info")?.Str("role") != role) return null;

            return OpenCodeTextParts(root.Arr("parts"));
        } catch {
            return null;
        }
    }

    static string? OpenCodeTextParts(JsonElement? parts) {
        if (parts is not { } arr) return null;

        var pieces = new List<string>();
        foreach (var part in arr.EnumerateArray()) {
            if (part.Str("type") != "text") continue;
            if (part.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("ignored",   out var ign) && ign.ValueKind == JsonValueKind.True) continue;
            if (part.Str("text")?.Trim() is { Length: > 0 } t) pieces.Add(t);
        }

        return pieces.Count > 0 ? string.Join("\n", pieces) : null;
    }

    // ── Antigravity extractors (AI-1158) ────────────────────────────────────────
    //
    // Antigravity writes brain/<id>/.system_generated/logs/transcript_full.jsonl as
    // {step_index, source, type, status, content, thinking, tool_calls, …} lines.
    // USER_INPUT (source USER_EXPLICIT) carries the prompt wrapped in
    // <USER_REQUEST>…</USER_REQUEST> plus trailing <ADDITIONAL_METADATA>/
    // <USER_SETTINGS_CHANGE> blocks; PLANNER_RESPONSE (source MODEL) carries the
    // assistant's visible `content`. Mirrors the server's
    // AntigravityTranscriptNormalizer so watcher titles match ingest.
    static string? TryExtractAntigravityText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            var want = role == "user" ? "USER_INPUT" : "PLANNER_RESPONSE";
            if (root.Str("type") != want) return null;

            if (root.Str("content")?.Trim() is not { Length: > 0 } content) return null;

            return role == "user" ? StripAntigravityUserWrapper(content) : content;
        } catch {
            return null;
        }
    }

    // Pull the human prompt out of the <USER_REQUEST> envelope, dropping the trailing
    // metadata blocks Antigravity appends. Falls back to the raw text when no envelope
    // is present.
    static string? StripAntigravityUserWrapper(string content) {
        const string open = "<USER_REQUEST>", close = "</USER_REQUEST>";
        var start = content.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return content;

        start += open.Length;
        var end   = content.IndexOf(close, start, StringComparison.Ordinal);
        var inner = (end < 0 ? content[start..] : content[start..end]).Trim();

        return inner.Length > 0 ? inner : null;
    }

    // Synthetic USAGE line numbers live in a high band so they never collide with real
    // transcript line numbers (which start at 0). The server derives the USAGE event id
    // from line CONTENT (which carries gen_row), so the line number is only an ordering
    // hint — a stable, non-colliding value keeps re-sends idempotent.
    const long AntigravityUsageLineBase = 1_000_000_000L;

    /// <summary>
    /// Appends synthetic USAGE lines for gen_metadata rows past the committed watermark, and
    /// returns the max row idx staged (-1 if none). It does NOT advance the watermark — the
    /// caller commits it only after the batch send succeeds, so a failed send re-reads the
    /// rows next drain. The db is the sibling of the transcript (its path carries the real
    /// conversation id, even when the session id is the canonical dashless form).
    /// </summary>
    static long AppendAntigravityUsageLines(WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string? createdAt) {
        var maxIdx = -1L;
        try {
            if (AntigravityPaths.ConversationDbFromTranscript(transcriptPath) is not { } dbPath) return -1L;
            var rows = AntigravityGenMetadataDb.ReadUsageLines(dbPath, state.LastAntigravityGenIdx, createdAt);

            foreach (var (idx, line) in rows) {
                newLines.Add(line);
                newLineNumbers.Add((int)(AntigravityUsageLineBase + idx));
                if (idx > maxIdx) maxIdx = idx;
            }
        } catch (Exception ex) {
            // Cost is always best-effort (AI-728) — never let a db read break the drain.
            Log($"Antigravity usage poll failed: {ex.Message}");
        }
        return maxIdx;
    }

    /// <summary>
    /// Tracks Antigravity tool calls in flight from a transcript line: a PLANNER_RESPONSE adds
    /// its tool_calls count; a result step (RUN_COMMAND/VIEW_FILE/LIST_DIRECTORY/CODE_ACTION)
    /// removes one. Safe to call for any line — non-matching / malformed lines are ignored — so
    /// the count reflects whether a (possibly long-running) tool is awaiting its result.
    /// </summary>
    internal static void UpdateAntigravityPendingToolCalls(WatchState state, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            switch (root.Str("type")) {
                case "PLANNER_RESPONSE":
                    if (root.Arr("tool_calls") is { } calls)
                        state.PendingAntigravityToolCalls += calls.EnumerateArray().Count(tc => tc.Str("name") is not null);
                    break;
                case "RUN_COMMAND" or "VIEW_FILE" or "LIST_DIRECTORY" or "CODE_ACTION":
                    if (state.PendingAntigravityToolCalls > 0) state.PendingAntigravityToolCalls--;
                    break;
            }
        } catch {
            // Never break the drain loop on a malformed line.
        }
    }

    static string? TryGetAntigravityCreatedAt(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Str("created_at") : null;
        } catch {
            return null;
        }
    }

    // ── Kiro extractors (AI-888) ───────────────────────────────────────────
    //
    // Kiro CLI writes ~/.kiro/sessions/cli/{id}.jsonl as {"version","kind","data"}
    // lines: kind "Prompt" (user) / "AssistantMessage" / "ToolResults", whose
    // data.content[] holds {"kind":"text"|"toolUse"|"toolResult","data":…} blocks.
    // For titles we want the first user prompt and the first assistant text.

    static string? TryExtractKiroUserText(string line) => KiroLineText(line, "Prompt");

    static string? TryExtractKiroAssistantText(string line) => KiroLineText(line, "AssistantMessage");

    static string? KiroLineText(string line, string kind) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("kind") != kind) return null;
            if (root.Obj("data")?.Arr("content") is not { } content) return null;

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("kind") == "text" && block.Str("data")?.Trim() is { Length: > 0 } text)
                    return text;
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    // ── Copilot extractors (AI-815) ────────────────────────────────────────
    //
    // Copilot's events.jsonl envelope: {type, data, id, timestamp, parentId,
    // agentId?}. Subagent-tagged events (agentId set) are skipped — a
    // subagent's user.message is the parent's task prompt, not something the
    // human typed, and using it for the title would mislabel the session.

    static string? TryExtractCopilotUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user.message") return null;
            if (root.Str("agentId") is not null) return null;

            // data.content is the clean prompt; transformedContent wraps it in
            // injected <current_datetime>/<system_reminder> noise.
            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    static string? TryExtractCopilotAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant.message") return null;
            if (root.Str("agentId") is not null) return null;

            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    // ── Pi extractors (AI-886) ─────────────────────────────────────────────
    //
    // Pi emits type:"message" with message.role user/assistant — NOT Claude's
    // top-level type:"user"/"assistant" — so the watcher title path needs its
    // own branch (mirrors the server PiTranscriptNormalizer mapping). Content is
    // a string or an array of {type:"text",text} blocks.

    static string? TryExtractPiUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "user") return null;

            if (msg.Str("content")?.Trim() is { Length: > 0 } strContent) return strContent;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractPiAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "assistant") return null;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractClaudeUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user") {
                return null;
            }

            // Skip system-injected meta messages (e.g. <local-command-caveat>)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True) {
                return null;
            }

            var msg = root.Obj("message");

            // message.content can be a string or an array
            if (msg?.Str("content") is { } strContent) {
                return strContent.StartsWith("<local-command-stdout>") ? null : StripSystemInstructions(strContent);
            }

            if (msg?.Arr("content") is { } arrContent) {
                foreach (var element in arrContent.EnumerateArray()) {
                    if (element.Str("type") == "text" && element.Str("text") is { } text
                     && !text.StartsWith("<local-command-stdout>")) {
                        return StripSystemInstructions(text);
                    }
                }
            }
        } catch (Exception ex) {
            if (!parseErrorLogged) {
                parseErrorLogged = true;
                Log($"TryExtractUserText parse error (further errors suppressed): {ex.Message}");
            }
        }

        return null;
    }

    static string? TryExtractCodexUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "user") return null;

            // Skip codex-injected wrappers (environment_context, AGENTS.md, turn_aborted).
            // These role:"user" payloads precede the real prompt and would otherwise be
            // used as the title context. Prelude list and block extractor live in
            // TitleGenerator so the offline-import and live-watch paths stay in sync.
            var text = TitleGenerator.ExtractCodexBlockText(payload.Value, "input_text");

            return text is null || TitleGenerator.IsCodexInjectedUserPrelude(text) ? null : text;
        } catch (Exception ex) {
            if (!parseErrorLogged) {
                parseErrorLogged = true;
                Log($"TryExtractUserText (codex) parse error (further errors suppressed): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Strips XML-like system instruction blocks from user text so they don't pollute title generation.
    /// Removes content between tags like &lt;system_instructions&gt;, &lt;system-reminder&gt;, etc.
    /// </summary>
    internal static string? StripSystemInstructions(string? text) {
        if (text is null) {
            return null;
        }

        var stripped = SystemInstructionsRegex.Replace(text, "").Trim();

        return stripped.Length > 0 ? stripped : null;
    }

    [GeneratedRegex(@"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemInstructionsRx();

    static readonly Regex SystemInstructionsRegex = SystemInstructionsRx();

    static async Task GenerateTitleAsync(HubConnection hubConnection, string sessionId, WatchState state, string vendor) {
        try {
            var result = await TitleGenerator.GenerateAsync(state.FirstUserText!, state.FirstAssistantText, Log, vendor);

            if (result is null) {
                Log($"Title generation attempt {state.TitleAttempts}/5 returned no usable result (CLI failure, refusal-like output, or empty title)");
                state.TitleInFlight = false;

                return;
            }

            Log($"Title usage: model={result.Model} input={result.InputTokens} output={result.OutputTokens} cost=${result.CostUsd:F4}");

            await PostTitleAsync(
                hubConnection,
                sessionId,
                result.Result,
                result.Model,
                result.InputTokens,
                result.OutputTokens,
                result.CacheReadTokens,
                result.CacheWriteTokens,
                state
            );
        } catch (Exception ex) {
            Log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    static async Task SendInitialTitleAsync(HubConnection hubConnection, string sessionId, string title) {
        try {
            await hubConnection.InvokeAsync("SendTitle", sessionId, title, null, 0L, 0L, 0L, 0L);
            Log($"Initial title sent: {title}");
        } catch (Exception ex) {
            Log($"Initial title send failed: {ex.Message}");
        }
    }

    static async Task PostTitleAsync(
            HubConnection hubConnection,
            string        sessionId,
            string        title,
            string?       model,
            long          inputTokens,
            long          outputTokens,
            long          cacheReadTokens,
            long          cacheWriteTokens,
            WatchState    state
        ) {
        try {
            await hubConnection.InvokeAsync("UpdateTitle", sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
            Log($"LLM title generated: {title}");
            state.TitleGenerated = true;
        } catch (Exception ex) {
            Log($"LLM title send failed: {ex.Message}");
        }

        state.TitleInFlight = false;
    }

    internal static bool RepoPayloadChanged(RepositoryPayload? current, RepositoryPayload? lastSent) {
        if (current is null) {
            return false;
        }

        if (lastSent is null) {
            return true;
        }

        return current.Owner  != lastSent.Owner
         || current.RepoName  != lastSent.RepoName
         || current.Branch    != lastSent.Branch
         || current.PrNumber  != lastSent.PrNumber
         || current.PrUrl     != lastSent.PrUrl
         || current.PrTitle   != lastSent.PrTitle
         || current.PrHeadRef != lastSent.PrHeadRef;
    }

    static string TruncateForTitle(string text, int maxLength) {
        // Take first line only, then truncate
        var firstLine  = text.AsSpan();
        var newlineIdx = firstLine.IndexOfAny('\r', '\n');

        if (newlineIdx >= 0) {
            firstLine = firstLine[..newlineIdx];
        }

        if (firstLine.Length <= maxLength) {
            return firstLine.ToString().Trim();
        }

        // Find last word boundary before maxLength
        var truncated = firstLine[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2) {
            truncated = truncated[..lastSpace];
        }

        return $"{truncated.ToString().Trim()}...";
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [watch] {message}");

    /// <summary>
    /// Result of a transcript drain read: the new non-blank <see cref="Lines"/> (beyond the
    /// caller's processed position), their 0-based <see cref="LineNumbers"/>, and the
    /// <see cref="NextPosition"/> the caller should advance its watermark to.
    /// </summary>
    public readonly record struct NewTranscriptLines(List<string> Lines, List<int> LineNumbers, int NextPosition);

    /// <summary>
    /// Split <paramref name="fileText"/> into the new complete (newline-terminated) transcript
    /// lines beyond <paramref name="linesProcessed"/>. A final line that is NOT newline-terminated
    /// means the agent is mid-write of it: it is held back — excluded from the batch AND from
    /// <see cref="NewTranscriptLines.NextPosition"/> — so a later drain re-reads it once complete.
    /// Consuming it would send a truncated line that fails to normalize server-side and permanently
    /// drop the completed line (AI-1243, the "endless reads" bug; large Read tool_result lines were
    /// the common victim). Blank lines are skipped from the output but still advance the position,
    /// matching the long-standing drain behaviour.
    /// </summary>
    public static NewTranscriptLines SplitNewCompleteLines(
            string fileText,
            int    linesProcessed,
            bool   holdIncompleteFinalLine = true
        ) {
        var newLines       = new List<string>();
        var newLineNumbers = new List<int>();

        var endsWithNewline = fileText.Length == 0 || fileText[^1] == '\n';

        using var reader    = new StringReader(fileText);
        var       lineIndex = 0;

        while (reader.ReadLine() is { } line) {
            if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                newLines.Add(line);
                newLineNumbers.Add(lineIndex);
            }

            lineIndex++;
        }

        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, holdIncompleteFinalLine);
    }

    /// <summary>
    /// Streams the new complete transcript lines from <paramref name="stream"/> WITHOUT materializing
    /// the whole file (Qodo #291 #2): only lines beyond <paramref name="linesProcessed"/> are retained.
    /// The file length is sampled once and the end-of-file newline is read from the last byte, then the
    /// read is CAPPED at that length — so a concurrent append after the sample can't make an as-yet
    /// unterminated final line look complete and get consumed (which would re-drop it — AI-1243).
    /// Opened by the caller with FileShare.ReadWrite (Qodo #291 #1) so the writing agent is never blocked.
    /// </summary>
    internal static async Task<NewTranscriptLines> ReadNewCompleteLinesAsync(
            FileStream        stream,
            int               linesProcessed,
            bool              holdIncompleteFinalLine,
            CancellationToken ct) {
        var length = stream.Length;

        bool endsWithNewline;
        if (length == 0) {
            endsWithNewline = true;
        } else {
            stream.Seek(length - 1, SeekOrigin.Begin);
            endsWithNewline = stream.ReadByte() == '\n';
            stream.Seek(0, SeekOrigin.Begin);
        }

        var newLines       = new List<string>();
        var newLineNumbers = new List<int>();
        var lineIndex      = 0;

        using var reader = new StreamReader(new LengthLimitedReadStream(stream, length), leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line) {
            if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                newLines.Add(line);
                newLineNumbers.Add(lineIndex);
            }

            lineIndex++;
        }

        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, holdIncompleteFinalLine);
    }

    // Hold back a still-being-written final line (no trailing newline yet): keep the position
    // before it and drop it from this batch if it was collected. The final drain at session end
    // opts out (holdIncompleteFinalLine: false) — the file is static then, so an unterminated
    // last line is genuinely the end and must be delivered, not lost. Shared by the string helper
    // (SplitNewCompleteLines) and the streaming reader (ReadNewCompleteLinesAsync).
    static NewTranscriptLines ApplyPartialLineHoldback(
            List<string> newLines,
            List<int>    newLineNumbers,
            int          nextPosition,
            int          linesProcessed,
            bool         endsWithNewline,
            bool         holdIncompleteFinalLine) {
        if (holdIncompleteFinalLine && !endsWithNewline && nextPosition > linesProcessed) {
            var partialIndex = nextPosition - 1;

            if (newLineNumbers.Count > 0 && newLineNumbers[^1] == partialIndex) {
                newLines.RemoveAt(newLines.Count - 1);
                newLineNumbers.RemoveAt(newLineNumbers.Count - 1);
            }

            nextPosition = partialIndex;
        }

        return new NewTranscriptLines(newLines, newLineNumbers, nextPosition);
    }

    /// <summary>Read-only view of <c>inner</c> that reports EOF after <c>limit</c> bytes, so a
    /// StreamReader over it can't read past a length sampled before a concurrent append.</summary>
    internal sealed class LengthLimitedReadStream(Stream inner, long limit) : Stream {
        long _consumed;

        public override int Read(byte[] buffer, int offset, int count) {
            var allowed = (int)Math.Min(count, limit - _consumed);
            if (allowed <= 0) return 0;
            var n = inner.Read(buffer, offset, allowed);
            _consumed += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            var allowed = (int)Math.Min(buffer.Length, limit - _consumed);
            if (allowed <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..allowed], ct);
            _consumed += n;
            return n;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => limit;

        public override long Position {
            get => _consumed;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static int CountFileLines(string path) {
        try {
            if (!File.Exists(path)) {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;

            while (reader.ReadLine() is not null) {
                count++;
            }

            return count;
        } catch {
            return 0;
        }
    }

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Compiled)]
    private static partial Regex CommandNameRx();

    /// <summary>
    /// Retries SignalR reconnection indefinitely with exponential backoff capped at 30s.
    /// The watcher is lightweight when idle — it should never give up while the session is active.
    /// </summary>
    sealed class InfiniteRetryPolicy : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount switch {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
    }
}
