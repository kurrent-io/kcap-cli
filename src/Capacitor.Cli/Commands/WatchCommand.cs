using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
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
            await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, vendor, CancellationToken.None);
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
        if (Volatile.Read(ref parentExited) == 1 && agentId is null && state.ThresholdReached) {
            await PostSessionEndOnParentExitAsync(baseUrl, sessionId, transcriptPath, cwd, vendor, state.Repository);
        }

        await logWriter.DisposeAsync();

        return 0;
    }

    /// <summary>
    /// Whitelist of vendor values accepted by the server's session-end route.
    /// Used to reject unexpected --vendor input before interpolating into the URL
    /// path (defence-in-depth against path traversal even though the CLI runs locally).
    /// </summary>
    static readonly HashSet<string> KnownVendors = new(StringComparer.Ordinal) { "claude", "codex", "copilot", "gemini", "pi" };

    /// <summary>
    /// Total time budget for the parent-exit session-end POST. Covers /auth/config
    /// discovery + retrying POST. Short by design — this runs on the watcher's
    /// shutdown path; a stalled server must not block process termination, the whole
    /// point of the parent-PID watchdog is to self-terminate within ~5s.
    /// </summary>
    static readonly TimeSpan ParentExitPostBudget = TimeSpan.FromSeconds(10);

    internal static async Task PostSessionEndOnParentExitAsync(
            string             baseUrl,
            string             sessionId,
            string             transcriptPath,
            string?            cwd,
            string             vendor,
            RepositoryPayload? repository
        ) {
        if (!KnownVendors.Contains(vendor)) {
            Log($"Parent-exit session-end skipped: unknown vendor '{vendor}'");
            return;
        }

        using var budgetCts = new CancellationTokenSource(ParentExitPostBudget);

        try {
            var endHook = new JsonObject {
                ["session_id"]      = sessionId,
                ["transcript_path"] = transcriptPath,
                ["cwd"]             = cwd ?? "",
                ["hook_event_name"] = "session_end",
                ["reason"]          = "parent_exited",
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
            CancellationToken ct
        ) {
        try {
            if (!File.Exists(transcriptPath)) {
                return;
            }

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();

            await using var stream = new FileStream(
                transcriptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(stream);

            var lineIndex = 0;

            while (await reader.ReadLineAsync(ct) is { } line) {
                if (lineIndex < state.LinesProcessed) {
                    lineIndex++;

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(SecretRedactor.RedactLine(line));
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            var linesRead = lineIndex;

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
            "codex"   => TryExtractCodexAssistantText(line),
            "copilot" => TryExtractCopilotAssistantText(line),
            "pi"      => TryExtractPiAssistantText(line),
            _         => TryExtractClaudeAssistantText(line)
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

            return root.Str("type") is "user" or "assistant";
        } catch {
            return false;
        }
    }

    internal static string? TryExtractUserText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"   => TryExtractCodexUserText(line),
            "copilot" => TryExtractCopilotUserText(line),
            "pi"      => TryExtractPiUserText(line),
            _         => TryExtractClaudeUserText(line)
        };

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
