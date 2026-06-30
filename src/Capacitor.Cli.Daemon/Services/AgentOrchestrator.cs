using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

public record AgentInstance(
        string                  Id,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string                  RepoPath,
        string                  Vendor,
        IPtyProcess             Process,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    public string?              SessionId         { get; set; }
    public string               Status            { get; set; } = "Starting";
    public DateTime             CreatedAt         { get; }      = DateTime.UtcNow;
    public DateTime             LastOutputAt      { get; set; } = DateTime.UtcNow;
    public bool                 HasReceivedOutput { get; set; }
    public TerminalOutputBuffer OutputBuffer      { get; } = new();

    /// <summary>Temp MCP config path written for hosted PR reviews; deleted on cleanup.</summary>
    public string? McpConfigPath { get; set; }

    /// <summary>
    /// Reason string sent to the server when ending the AgentSession. Defaults to
    /// "agent_exited" (claude exited on its own); HandleStopAgent flips it to
    /// "agent_stopped" so a user-initiated stop is still attributed correctly even
    /// if HandleStopAgent's own EndAgentSessionAsync call fails and the read-loop's
    /// finally-block call is the only one that lands.
    /// </summary>
    public string PendingEndReason { get; set; } = "agent_exited";

    // ── Local terminal attach (Phase 1) ──────────────────────────────────
    // Internal: these expose the daemon-internal ITerminalSink, so they can't be public
    // on this public record (CS0053). They're only touched inside the daemon assembly.
    /// <summary>Local-terminal clients attached over the control socket.</summary>
    internal List<ITerminalSink> LocalSinks { get; } = [];
    internal Lock                SinksLock  { get; } = new();
    /// <summary>Each attached local client's last-reported size, for the resize min-clamp.</summary>
    internal Dictionary<ITerminalSink, Dim> ClientDims { get; } = [];
    public readonly record struct Dim(ushort Cols, ushort Rows);

    /// <summary>The server-aggregated min size across all web viewers (one value per agent,
    /// computed server-side from per-connection web dims), folded into the same min-clamp as the
    /// local clients so a small web viewer and a large local terminal share the one PTY at the
    /// smallest size — tmux semantics across surfaces. <c>null</c> when no web viewer is
    /// attached, so the clamp grows back to the local-only size. Guarded by <see cref="SinksLock"/>.</summary>
    internal Dim? WebDims { get; set; }

    /// <summary>Tripped when the agent terminates (CleanupAgentAsync) so an attached local
    /// client that's blocked waiting on the user's keystrokes wakes, flushes the last output,
    /// and sends an Exited frame instead of hanging.</summary>
    internal CancellationTokenSource ExitedCts { get; } = new();

    /// <summary>
    /// True for locally-launched agents: the orchestrator makes no per-agent server call
    /// and does not attach the SignalR sink. An explicit share (Phase 2) clears this.
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// True for agents started from a local terminal (`kcap run-agent`), whether registered or
    /// `--private`. Such an agent has a live local terminal as its primary surface, so the read
    /// loop streams to the server <b>non-blocking</b> (drop+count on a full backlog) rather than
    /// back-pressuring the PTY on a remote tunnel stall — the local terminal must not freeze when
    /// the cloud hiccups. Hosted agents (server is the only consumer) keep lossless back-pressure.
    /// </summary>
    public bool IsLocalSpawned { get; init; }

    /// <summary>Owned worktree (daemon-created — safe to remove on cleanup) vs borrowed cwd
    /// (the user's own checkout — never removed).</summary>
    public WorkLocation Work { get; init; } = WorkLocation.OwnedWorktree;

    /// <summary>Current PTY dimensions — the single source of truth for every dims send
    /// (registration, reconnect). Updated by every resize path (local clamp + web resize).
    /// Hosted agents initialise these to the fixed HostedPtyCols/Rows; ushort read/write is
    /// atomic, and stale-by-one-resize is harmless for best-effort dims.</summary>
    public ushort CurrentCols { get; set; }
    public ushort CurrentRows { get; set; }
}

/// <summary>Ring buffer that keeps the last 2 MB of terminal output.</summary>
public class TerminalOutputBuffer {
    readonly List<byte[]> _chunks = [];
    int                   _totalBytes;
    const int             MaxBytes = 2 * 1024 * 1024;

    public void Append(byte[] data) {
        lock (_chunks) {
            _chunks.Add(data);
            _totalBytes += data.Length;

            while (_totalBytes > MaxBytes && _chunks.Count > 1) {
                _totalBytes -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    public List<byte[]> GetAll() {
        lock (_chunks) { return [.._chunks]; }
    }

    /// <summary>Flattens the retained ring into one buffer for a one-time replay to a
    /// newly-attached client (bounded by <see cref="MaxBytes"/>).</summary>
    public byte[] Snapshot() {
        lock (_chunks) {
            var ms = new MemoryStream(_totalBytes);
            foreach (var c in _chunks) ms.Write(c);

            return ms.ToArray();
        }
    }
}

internal partial class AgentOrchestrator : IAsyncDisposable {
    readonly ConcurrentDictionary<string, AgentInstance>       _agents = new();
    readonly DaemonConfig                                      _config;
    readonly ServerConnection                                  _server;
    readonly WorktreeManager                                   _worktreeManager;
    readonly RepoMatcher                                       _repoMatcher;
    readonly IPtyProcessFactory                                _ptyFactory;
    readonly IHttpClientFactory                                _httpClientFactory;
    readonly LocalPermissionBridge                             _permissionBridge;
    readonly IReadOnlyDictionary<string, IHostedAgentLauncher> _launchers;
    readonly ILogger<AgentOrchestrator>                        _logger;

    // Hosted-agent PTYs are spawned at a fixed size and never resized. The daemon
    // reports these dims to the server right after the agent registers (and on
    // reconnect) so the read-only viewers (web/desktop xterm) lock to exactly the
    // width Claude drew for — otherwise the viewer auto-fits its panel and the
    // mismatched columns garble the TUI (AI-884). PtyDefaults is the single source
    // of truth, shared with IPtyProcessFactory.Spawn's defaults so they can't drift.
    const ushort HostedPtyCols = PtyDefaults.Cols;
    const ushort HostedPtyRows = PtyDefaults.Rows;

    readonly PeriodicTimer _heartbeatTimer = new(TimeSpan.FromSeconds(30));

    // AI-79: heartbeat tightened from 60 s SendAsync to round-trip Ping.
    // AI-642: tick halved (15 → 7 s) and deadline halved (10 → 5 s) so a
    // displaced-slot mismatch or a hung transport is caught within ~10 s
    // instead of ~25 s. This is independent of SignalR's transport timeout
    // (which stays at the 30 s default) — the heartbeat is the daemon's
    // application-level liveness probe.
    readonly PeriodicTimer _daemonHeartbeat = new(TimeSpan.FromSeconds(7));

    static readonly TimeSpan PingDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <see cref="FinalizeAgentRunAsync"/> waits on the (reconnect-retrying)
    /// EndAgentSession call before proceeding with local cleanup regardless. Covers a
    /// typical transient SignalR blip with margin; a longer outage proceeds to cleanup
    /// while the retry continues in the background. Settable so tests don't wait 30s.
    /// </summary>
    internal TimeSpan EndAgentSessionBudget { get; set; } = TimeSpan.FromSeconds(30);

    // Linked to IHostApplicationLifetime.ApplicationStopping so the shutdown gate
    // trips as soon as the host begins stopping — the same instant ServerConnection's
    // _ct (also ApplicationStopping) cancels SignalR calls. Otherwise there's a
    // window between ApplicationStopping firing and DisposeAsync running where
    // server calls would still throw TaskCanceledException unguarded.
    readonly CancellationTokenSource _shutdownCts;

    public AgentOrchestrator(
            DaemonConfig                                      config,
            ServerConnection                                  server,
            WorktreeManager                                   worktreeManager,
            RepoMatcher                                       repoMatcher,
            IPtyProcessFactory                                ptyFactory,
            IHttpClientFactory                                httpClientFactory,
            LocalPermissionBridge                             permissionBridge,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            IHostApplicationLifetime                          lifetime,
            ILogger<AgentOrchestrator>                        logger
        ) {
        _shutdownCts       = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _config            = config;
        _server            = server;
        _worktreeManager   = worktreeManager;
        _repoMatcher       = repoMatcher;
        _ptyFactory        = ptyFactory;
        _httpClientFactory = httpClientFactory;
        _permissionBridge  = permissionBridge;
        _launchers         = launchers;
        _logger            = logger;

        // Wire up server commands
        _server.OnLaunchAgent            += HandleLaunchAgent;
        _server.OnStopAgent              += HandleStopAgent;
        _server.OnSendInput              += HandleSendInput;
        _server.OnSendSpecialKey         += HandleSendSpecialKey;
        _server.OnResizeTerminal         += HandleResizeTerminal;
        _server.ReRegisterAgentsHook          =  ReRegisterAgentsAsync;
        _server.FindRepoForRemoteHandler      =  HandleFindRepoForRemote;
        _server.RefreshAgentWorktreeHandler   =  HandleRefreshAgentWorktree;

        _server.GetLiveAgentIds = () => [
            .. _agents
                .Where(kvp => (kvp.Value.Status is "Starting" or "Running") && !kvp.Value.IsPrivate)
                .Select(kvp => kvp.Key)
        ];

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
    }

    internal int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var agentId       = cmd.AgentId;
        var prompt        = cmd.Prompt;
        var model         = cmd.Model;
        var effort        = cmd.Effort;
        var repoPath      = cmd.RepoPath;
        var tools         = cmd.Tools;
        var attachmentIds = cmd.AttachmentIds;
        var vendor        = cmd.Vendor;
        var isReview      = cmd.Kind == LaunchKind.Review;

        if (cmd.Vendor is not ("claude" or "codex")) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");

            return;
        }

        if (!_launchers.TryGetValue(cmd.Vendor, out var launcher)) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"No launcher registered for vendor: {cmd.Vendor}");

            return;
        }

        WorktreeInfo? worktree      = null;
        string?       mcpConfigPath = null;

        try {
            if (ActiveCount >= _config.MaxConcurrentAgents) {
                await _server.LaunchFailedAsync(agentId, $"At max capacity ({_config.MaxConcurrentAgents} agents)");

                return;
            }

            if (!_config.IsRepoAllowed(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path not allowed: {repoPath}");

                return;
            }

            if (!Directory.Exists(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path does not exist: {repoPath}");

                return;
            }

            if (isReview) {
                if (cmd.Review is not { } review) {
                    await _server.LaunchFailedAsync(agentId, "Review launch missing PR info");

                    return;
                }

                // Final guard: re-validate that the chosen path's origin really
                // matches the PR's repo. The match the UI saw could have moved
                // (remote renamed, repo moved) between picker and launch.
                var actual = await GetOriginRemoteAsync(repoPath);

                if (actual is null) {
                    await _server.LaunchFailedAsync(agentId, $"No origin remote at {repoPath}");

                    return;
                }

                var expected = $"github.com/{review.Owner}/{review.Repo}";

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                    await _server.LaunchFailedAsync(agentId, $"Repo at {repoPath} no longer matches {review.Owner}/{review.Repo} (origin: {actual})");

                    return;
                }
            }

            // "auto" means let the CLI decide — don't pass --effort at all
            if (string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
                effort = null;
            }

            // Validate effort level before expensive worktree setup
            if (!string.IsNullOrEmpty(effort) && !ValidEffortLevels.Contains(effort)) {
                await _server.LaunchFailedAsync(agentId, $"Invalid effort level: {effort}");

                return;
            }

            LogLaunching(agentId, repoPath, effort ?? "default", model);

            // Review launches base the worktree on the PR head ref so the agent
            // works against the PR's actual state, not the local HEAD.
            var baseRef = isReview && cmd.Review is { } reviewInfo
                ? $"refs/pull/{reviewInfo.PrNumber}/head"
                : cmd.BaseRef;

            worktree = await _worktreeManager.CreateAsync(repoPath, baseRef: baseRef);

            // Download attachments into worktree (best-effort)
            if (attachmentIds is { Length: > 0 }) {
                try {
                    var paths = await DownloadAttachmentsAsync(worktree.Path, attachmentIds);

                    if (paths.Count > 0) {
                        var suffix = $"\n\n[Attached files: {string.Join(", ", paths)}]";
                        prompt = string.IsNullOrEmpty(prompt) ? suffix.TrimStart() : prompt + suffix;
                    }
                } catch (Exception ex) {
                    LogAttachmentDownloadFailed(ex, agentId);
                }
            }

            var launcherCtx = new LauncherContext(
                AgentId: agentId,
                SourceRepoPath: repoPath,
                Worktree: worktree,
                Prompt: prompt,
                Model: model,
                Effort: effort,
                Tools: tools,
                IsReview: isReview,
                Review: cmd.Review,
                ReviewLaunch: isReview && cmd.Review is { } reviewArgs
                    ? await ReviewLaunchBuilder.BuildAsync(cmd.Vendor, _config.CapacitorPath, _config.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
                    : null
            );

            try {
                launcher.Prepare(launcherCtx);
            } catch (CodexHooksNotInstalledException ex) {
                await _server.LaunchFailedAsync(agentId, ex.Message);

                // Still need to clean up the worktree before returning
                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }

                return;
            } catch (Exception ex) {
                LogPrepareSoftFailure(ex, agentId);
            }

            var launchArgs = launcher.BuildArgs(launcherCtx);
            var args       = launchArgs.Args;
            mcpConfigPath = launchArgs.McpConfigPath;

            var env = new Dictionary<string, string> {
                ["KCAP_RENDERED_AGENT"] = "1",
                ["KCAP_AGENT_ID"]       = agentId
            };

            if (!string.IsNullOrEmpty(_config.ServerUrl)) {
                env["KCAP_URL"] = _config.ServerUrl;
            }

            // Tell the spawned Claude's permission-request hook where to find this
            // daemon's local SignalR bridge. Bypasses Cloudflare's HTTP timeout on
            // the server's /hooks/permission-request long-poll. CLI falls back to
            // KCAP_URL if this var is absent (e.g. older CLI builds).
            if (_permissionBridge.BaseUrl is { } bridgeUrl) {
                env["KCAP_DAEMON_URL"] = bridgeUrl;
            }

            if (isReview && cmd.Review is { } reviewEnv) {
                env["KCAP_REVIEW_PR"] = reviewEnv.PrNumber.ToString();
            }

            var process = _ptyFactory.Spawn(launcher.CliPath, args, worktree.Path, env, HostedPtyCols, HostedPtyRows);

            LogAgentSpawned(agentId, process.Pid, worktree.Path, launcher.CliPath);

            var cts = new CancellationTokenSource();

            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, cmd.Vendor, process, worktree, cts) {
                McpConfigPath = mcpConfigPath,
                CurrentCols   = HostedPtyCols,
                CurrentRows   = HostedPtyRows
            };
            _agents[agentId] = agent;

            await RegisterAgentAsync(agent);

            // Start reading output
            _ = ReadAgentOutputAsync(agent);
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            if (worktree != null) {
                if (_launchers.TryGetValue(cmd.Vendor, out var launcherForCleanup)) {
                    try {
                        // Build a transient AgentInstance with a no-op PTY just so launcher.Cleanup
                        // can run its symlink/mcp-config teardown without a live agent.
                        var transient = new AgentInstance(
                            agentId,
                            prompt,
                            model,
                            effort,
                            repoPath,
                            cmd.Vendor,
                            NoopPtyProcess.Instance,
                            worktree,
                            new CancellationTokenSource()
                        ) {
                            McpConfigPath = mcpConfigPath
                        };
                        launcherForCleanup.Cleanup(transient);
                    } catch (Exception cleanupEx) {
                        LogCleanupStepFailed(cleanupEx, "launcher.Cleanup (failed-launch)", agentId);
                    }
                }

                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }
            }

            await _server.LaunchFailedAsync(agentId, ex.Message);
        }
    }

    static readonly TimeSpan GitGuardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads <c>git remote get-url origin</c> at <paramref name="repoPath"/>
    /// and normalises it to <c>host/owner/repo</c> form (or null if missing
    /// or if git times out / blocks on a credential prompt). Used as a final
    /// guard before a hosted PR review is launched, so it must never hang the
    /// launch path.
    /// </summary>
    static async Task<string?> GetOriginRemoteAsync(string repoPath) {
        try {
            var psi = new ProcessStartInfo("git", ["remote", "get-url", "origin"]) {
                WorkingDirectory       = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                Environment = {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GCM_INTERACTIVE"]     = "Never"
                }
            };

            using var proc = Process.Start(psi);

            if (proc is null) return null;

            using var cts = new CancellationTokenSource(GitGuardTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return null;
            }

            if (proc.ExitCode != 0) return null;

            var raw = (await proc.StandardOutput.ReadToEndAsync()).Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : RemoteMatcher.NormalizeRemoteUrl(raw);
        } catch {
            return null;
        }
    }

    async Task ReadAgentOutputAsync(AgentInstance agent) {
        // The terminal-output enqueue back-pressures (awaits) when the send queue is
        // full. Tie that await to BOTH this agent's stop (ReadCts) and daemon shutdown
        // so HandleStopAgent releasing ReadCts unblocks the read loop — otherwise a
        // stop mid-outage would leave the finally-block finalization/cleanup stalled
        // until the whole daemon exits (AI-846).
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);

        try {
            await foreach (var data in agent.Process.ReadOutputAsync(agent.ReadCts.Token)) {
                agent.LastOutputAt      = DateTime.UtcNow;
                agent.HasReceivedOutput = true;

                if (agent.Status == "Starting") {
                    agent.Status = "Running";
                    if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
                }

                // Append to the replay buffer AND fan out to local sinks atomically under
                // SinksLock — paired with attach taking its snapshot + subscribing under the
                // same lock, so a chunk can't land in both a new client's replay and its live
                // stream (duplication), nor in neither (gap). TryEnqueue is non-blocking so the
                // lock is held only briefly; a slow client force-detaches inside TryEnqueue.
                lock (agent.SinksLock) {
                    agent.OutputBuffer.Append(data);
                    foreach (var sink in agent.LocalSinks) sink.TryEnqueue(data);
                }

                if (!agent.IsPrivate) {
                    var base64 = Convert.ToBase64String(data);

                    if (agent.IsLocalSpawned) {
                        // Local-first: a registered local agent has a live local terminal as its
                        // primary surface, so NEVER block the PTY read loop on a remote tunnel
                        // stall. Enqueue non-blocking; a full backlog (sustained outage) drops +
                        // counts the chunk and the web mirror re-syncs from the server's own buffer
                        // on reconnect. Keeps the local terminal responsive when the cloud hiccups.
                        _server.TrySendTerminalOutput(agent.Id, base64);
                    } else {
                        // Hosted: the server is the only consumer, so back-pressure here when the
                        // queue is full (slow/down transport) — a chunk is never dropped, since
                        // losing one byte garbles the whole redraw-TUI mirror (AI-844). sendCts
                        // releases this await on agent stop or daemon shutdown (AI-846).
                        await _server.SendTerminalOutputAsync(agent.Id, base64, sendCts.Token);
                    }
                }
            }
        } catch (OperationCanceledException) {
            /* expected on stop */
        } catch (Exception ex) {
            LogOutputReadError(ex, agent.Id);
        } finally {
            // Daemon shutdown: ServerConnection's _ct (= lifetime.ApplicationStopping)
            // is cancelled and the hub is being disposed, so every server call here
            // would throw TaskCanceledException. DisposeAsync owns the local cleanup
            // path for in-flight agents; the server detects the daemon disconnection
            // and ends its sessions on its own. Skip to avoid noisy warnings.
            if (!_shutdownCts.IsCancellationRequested) {
                await FinalizeAgentRunAsync(agent);
            }
        }
    }

    async Task FinalizeAgentRunAsync(AgentInstance agent) {
        try {
            // PTY output can end before waitpid reports the child as exited.
            // Wait briefly for the process to finalize so we get a real exit code.
            await agent.Process.WaitForExitAsync(TimeSpan.FromSeconds(5));

            var exitCode = agent.Process.ExitCode;

            var status = agent.Process.HasExited
                ? exitCode is null or 0 ? "Completed" : "Failed"
                : "Failed";

            if (agent.Status is not "Completed" and not "Failed") {
                // A startup failure means the process exited before establishing
                // a real interactive session (CLI config error, auth issue, immediate
                // crash). A real session keeps producing output throughout its
                // lifetime, so the gap between CreatedAt and LastOutputAt is the
                // discriminator: tiny gap → startup failure; sustained → real session.
                //
                // We avoid agent.Status because the first output chunk flips it to
                // "Running" — a one-line error banner triggers that flip too. We
                // also avoid wall-clock since spawn: a user who types /exit shortly
                // after starting produces a short-but-real session that must not be
                // flagged as a launch failure. HasReceivedOutput guards
                // against a no-output process whose CreatedAt/LastOutputAt
                // initializers happened to straddle a long pause.
                if (IsStartupFailure(agent.CreatedAt, agent.LastOutputAt, agent.HasReceivedOutput)) {
                    var output = ExtractTerminalText(agent.OutputBuffer);

                    var reason = !string.IsNullOrWhiteSpace(output)
                        ? output
                        : exitCode is null or 0
                            ? "Process exited before establishing a session"
                            : $"Process exited immediately (exit code {exitCode})";

                    status = "Failed";

                    LogStartupFailed(agent.Id, exitCode, reason);

                    if (!agent.IsPrivate) _ = _server.LaunchFailedAsync(agent.Id, reason);
                }

                agent.Status = status;

                // PrivateLocal agents make no per-agent server calls (deny-all).
                if (!agent.IsPrivate) {
                    await _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunStopped(stopReason, exitCode));
                }
            }

            LogAgentExited(agent.Id, exitCode);

            // Tell the server to end the AgentSession. Claude doesn't reliably fire
            // its own session-end hook on SIGTERM/exit, so without this call the
            // session would stay "active" forever in the read model. Server-side is
            // idempotent — if claude did fire session-end first, this is a no-op.
            // Reason is read from agent.PendingEndReason so a user-initiated stop is
            // recorded as "agent_stopped" rather than "agent_exited".
            //
            // EndAgentSessionAsync retries across SignalR reconnects, so it can block
            // for the length of an outage. We must NOT let that stall local cleanup
            // (worktree/process disposal, removing the agent from _agents), so bound how
            // long we WAIT on it to EndAgentSessionBudget. The retry keeps running in the
            // background — a reconnect shortly after still lands the session-end — and a
            // genuinely long outage falls back to server-side daemon-disconnect reconcile.
            //
            // PrivateLocal agents have no server-side session to end (deny-all).
            if (!agent.IsPrivate) {
                var endTask = _server.EndAgentSessionAsync(agent.Id, agent.PendingEndReason);

                try {
                    var result = await endTask.WaitAsync(EndAgentSessionBudget, _shutdownCts.Token);

                    // The daemon doesn't track sessionId on its own (only agentId), so
                    // the server returns it in the result. Spawn what's-done locally
                    // when the server says yes.
                    if (result is { GenerateWhatsDone: true, SessionId: not null }) {
                        SpawnWhatsDoneGenerator(result.SessionId);
                    }
                } catch (TimeoutException) {
                    // Outage outlasted the budget. Don't block cleanup; the retry continues
                    // in the background (observed below so a later fault isn't unobserved).
                    LogEndSessionTimedOut(agent.Id, EndAgentSessionBudget.TotalSeconds);
                    ObserveEndSessionInBackground(endTask, agent.Id);
                } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) {
                    // Shutdown fired mid-wait — connection is being torn down. Server
                    // detects daemon disconnection independently. No warning needed.
                } catch (Exception ex) {
                    LogEndSessionFailed(ex, agent.Id);
                }
            }

            // Clean up worktree and unregister from server. Runs unconditionally — even
            // when end-session timed out and is still retrying in the background — so a
            // prolonged outage can never pin the agent in _agents or leak its worktree.
            await CleanupAgentAsync(agent.Id);
        } catch (Exception ex) {
            LogCleanupError(ex, agent.Id);
        }
    }

    /// <summary>
    /// Initial wait after sending /exit to give claude a chance to flush its session-end
    /// hook (which writes SessionEnded plus the what's-done summary). 15s covers a typical
    /// session-end POST + watcher drain on a healthy connection.
    /// </summary>
    static readonly TimeSpan GracefulExitWait = TimeSpan.FromSeconds(15);

    async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        // Defence-in-depth: a --private agent is invisible to the server (unregistered, not in
        // LiveAgentIds), so never act on a server-origin command for one even if its id leaks.
        if (agent.IsPrivate) return;

        try {
            LogStopping(agentId);

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            agent.Status = "Completed";
            // Mark this as a user-initiated stop so the read-loop's finally-block
            // EndAgentSessionAsync call uses "agent_stopped" if it ends up being
            // the only successful call (e.g., transient SignalR failure here).
            agent.PendingEndReason = "agent_stopped";
            _                      = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
            _                      = _server.AppendAgentRunEventAsync(agentId, new AgentRunStopped("user", null));

            // Try a graceful shutdown first: send /exit so claude can fire its own
            // session-end hook (drains transcript, writes SessionEnded + summary,
            // optionally schedules what's-done). Falls through to SIGTERM/SIGKILL
            // below if claude doesn't exit in time.
            //
            // Claude CLI requires the slash-command text and the Enter key to arrive
            // as separate PTY writes (with a small delay between them) — sending them
            // in a single write makes Claude treat the carriage return as part of the
            // command buffer instead of a submit. HandleSendInput uses the same split
            // pattern; matching it here makes the graceful path actually fire.
            try {
                await agent.Process.WriteAsync("/exit");
                await Task.Delay(50);
                await agent.Process.WriteAsync("\r");
                await agent.Process.WaitForExitAsync(GracefulExitWait);
            } catch (Exception ex) {
                LogGracefulExitFailed(ex, agentId);
            }

            // PTY WaitForExitAsync(timeout) returns silently when the timeout elapses,
            // so a graceful-exit *timeout* doesn't throw. Check HasExited explicitly
            // so we can tell from logs whether the graceful path is actually working
            // in production or if claude is consistently being SIGTERMed instead.
            if (!agent.Process.HasExited) {
                LogGracefulExitTimedOut(agentId, GracefulExitWait.TotalSeconds);
            }

            // Cancel the read loop and terminate the process. We deliberately do NOT end
            // the AgentSession here: EndAgentSessionAsync now retries across SignalR
            // reconnects (so it can block while a dropped connection recovers), and a
            // user-initiated stop must not wait on that. Cancelling ReadCts unblocks the
            // read loop, whose finally block runs FinalizeAgentRunAsync once the process
            // exits — that ends the session (with retry) using agent.PendingEndReason
            // ("agent_stopped") and spawns the what's-done generator if the server asks.
            // So session-end is reliable as the post-exit backstop without delaying
            // teardown, and is idempotent: if claude already fired its own session-end
            // during the graceful window above, the backstop call is a server-side no-op.
            await agent.ReadCts.CancelAsync();
            await agent.Process.TerminateAsync(TimeSpan.FromSeconds(10));
        } catch (Exception ex) {
            LogStopError(ex, agentId);
        }
    }

    /// <summary>
    /// Observes a background EndAgentSession retry that outlived the finalize budget so a
    /// later fault isn't an unobserved task exception. Success and shutdown-cancellation
    /// are intentionally ignored; only a genuine fault is logged.
    /// </summary>
    void ObserveEndSessionInBackground(Task<EndAgentSessionResult> endTask, string agentId) =>
        _ = endTask.ContinueWith(
            t => LogEndSessionFailed(t.Exception!.GetBaseException(), agentId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>
    /// Spawns <c>kcap generate-whats-done {sessionId}</c> as a detached process.
    /// Used when the daemon-driven session-end path supplants claude's own session-end
    /// hook — claude normally spawns this generator from its CLI session-end handler,
    /// but when claude crashed or was killed before firing session-end the daemon has
    /// to do it instead. Best-effort: failure is logged but doesn't block other
    /// cleanup, and a missing kcap binary just means no what's-done summary.
    /// </summary>
    void SpawnWhatsDoneGenerator(string sessionId) {
        try {
            var psi = new ProcessStartInfo(_config.CapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"] = _config.ServerUrl
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            using var proc = Process.Start(psi);

            if (proc is null) {
                LogWhatsDoneSpawnFailed(null, sessionId);

                return;
            }

            // Detach: close redirected streams so we don't hold pipes for the child's
            // lifetime. The child runs to completion on its own and posts its result
            // to the server.
            proc.StandardInput.Close();
            proc.StandardOutput.Close();
            proc.StandardError.Close();

            LogWhatsDoneSpawned(sessionId, proc.Id);
        } catch (Exception ex) {
            LogWhatsDoneSpawnFailed(ex, sessionId);
        }
    }

    async Task HandleSendInput(SendInputCommand cmd) {
        var (agentId, text, attachmentIds) = cmd;

        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        if (agent.IsPrivate) return; // server-origin input ignored for private agents

        var message = text;

        if (attachmentIds is { Length: > 0 }) {
            var paths = await DownloadAttachmentsAsync(agent.Worktree.Path, attachmentIds);

            if (paths.Count > 0) {
                message = $"{text}\n\n[Attached files: {string.Join(", ", paths)}]";
            }
        }

        // Split text and Enter with delay (Claude CLI needs separate writes)
        await agent.Process.WriteAsync(message);
        await Task.Delay(50);
        await agent.Process.WriteAsync("\r");
    }

    async Task HandleSendSpecialKey(string agentId, string key) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        if (agent.IsPrivate) return; // server-origin key ignored for private agents

        var bytes = SpecialKeyMap.ToBytes(key);

        if (bytes.Length > 0) {
            await agent.Process.WriteAsync(bytes);
        }
    }

    async Task<List<string>> DownloadAttachmentsAsync(string worktreePath, string[] attachmentIds) {
        var attachDir = Path.Combine(worktreePath, ".attached");
        Directory.CreateDirectory(attachDir);

        // Write .gitignore to prevent accidental commits
        var gitignorePath = Path.Combine(attachDir, ".gitignore");

        if (!File.Exists(gitignorePath)) {
            await File.WriteAllTextAsync(gitignorePath, "*\n");
        }

        var paths = new List<string>();

        foreach (var id in attachmentIds) {
            try {
                using var httpClient = _httpClientFactory.CreateClient("Attachments");

                var tokens = await TokenStore.GetValidTokensAsync();

                if (tokens is not null) {
                    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
                }

                var response = await httpClient.GetAsync($"/api/attachments/{id}");

                if (!response.IsSuccessStatusCode) {
                    LogAttachmentNotFound(id, response.StatusCode);

                    continue;
                }

                var rawFileName = response.Content.Headers.ContentDisposition?.FileNameStar
                 ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                 ?? $"attachment-{id[..8]}";

                // Sanitize: strip path separators to prevent directory traversal
                var fileName = Path.GetFileName(rawFileName);

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"attachment-{id[..8]}";

                var filePath = GetUniqueFilePath(attachDir, fileName);
                var fullPath = Path.GetFullPath(filePath);
                var safeDir  = Path.GetFullPath(attachDir) + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(safeDir)) {
                    LogAttachmentPathEscape(rawFileName);

                    continue;
                }

                await using var fs = File.Create(filePath);
                await response.Content.CopyToAsync(fs);

                paths.Add($".attached/{Path.GetFileName(filePath)}");
            } catch (Exception ex) {
                LogAttachmentError(ex, id);
            }
        }

        return paths;
    }

    static string GetUniqueFilePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path)) {
            return path;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        var counter        = 2;

        do {
            path = Path.Combine(directory, $"{nameWithoutExt}-{counter}{ext}");
            counter++;
        } while (File.Exists(path));

        return path;
    }

    Task<string[]> HandleFindRepoForRemote(FindRepoForRemoteRequest req)
        => _repoMatcher.FindAsync(req.Owner, req.Repo, req.CandidatePaths ?? [], _shutdownCts.Token);

    /// <summary>
    /// Handles the server's <c>RefreshAgentWorktree</c> client-result invocation (AI-774).
    /// Syncs the source repo's current working-tree state into the reviewer agent's daemon-created
    /// worktree so the reviewer sees Claude's latest uncommitted changes before a follow-up round.
    /// Guards: agent must exist, must not be private, and must be an OwnedWorktree (not borrowed cwd).
    /// </summary>
    async Task<RefreshAgentWorktreeResult> HandleRefreshAgentWorktree(RefreshAgentWorktreeCommand cmd) {
        if (!_agents.TryGetValue(cmd.AgentId, out var agent))
            return new RefreshAgentWorktreeResult(false, "agent not found");

        if (agent.IsPrivate)
            return new RefreshAgentWorktreeResult(false, "private agent");

        if (agent.Work != WorkLocation.OwnedWorktree)
            return new RefreshAgentWorktreeResult(false, "not an owned worktree");

        try {
            await _worktreeManager.SyncFromSourceAsync(
                cmd.SourceRepoRoot,
                agent.Worktree.Path,
                cmd.ExcludePaths,
                _shutdownCts.Token
            );

            return new RefreshAgentWorktreeResult(true, null);
        } catch (Exception ex) {
            LogRefreshWorktreeFailed(ex, cmd.AgentId, cmd.SourceRepoRoot, agent.Worktree.Path);

            return new RefreshAgentWorktreeResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Registers an agent with the server exactly as a UI-launched agent: AgentRegistered +
    /// terminal dims + AgentRunStarted, then persists/announces the repo path. No-ops for a
    /// PrivateLocal agent. Shared by the hosted launch and the registered local launch so the
    /// two cannot drift. Dims come from <see cref="AgentInstance.CurrentCols"/>/<c>CurrentRows</c>
    /// (hosted = HostedPtyCols/Rows; local = the client's terminal size).
    /// </summary>
    async Task RegisterAgentAsync(AgentInstance agent) {
        if (agent.IsPrivate) return;

        await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath);

        // Report the PTY size so read-only viewers lock their xterm to it. Best-effort.
        try {
            await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
        } catch (Exception ex) {
            LogTerminalDimsSendFailed(ex, agent.Id);
        }

        _ = _server.AppendAgentRunEventAsync(
            agent.Id,
            new AgentRunStarted(agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.Worktree.Path, agent.Vendor)
        );

        // Persist repo path and notify server so the launch dialog updates.
        _ = Task.Run(async () => {
                try {
                    await RepoPathStore.AddAsync(agent.RepoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agent.Id);
                }
            }
        );
    }

    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        // Ignore server-origin resize for private agents (defence-in-depth; see HandleStopAgent).
        if (_agents.TryGetValue(cmd.AgentId, out var agent) && !agent.IsPrivate) {
            // The server sends the min aggregate across all web viewers, or (0,0) when the
            // last web viewer left. Fold it into the same min-clamp as the local clients rather than
            // resizing the PTY directly — a small web viewer must not corrupt a large local terminal
            // (or vice-versa), and a departing web viewer must let the PTY grow back to the local size.
            //
            // (0,0) clears WebDims. Accept other dims only when they're positive AND fit the PTY's
            // ushort winsize — ignore anything else (negative, or > ushort.MaxValue) so a bad value
            // can't wrap on the cast and poison the shared clamp (e.g. a wrapped 0 would block all
            // resizing). The server already bounds-checks; this is defence-in-depth — the daemon must
            // not trust the wire.
            var clear = cmd is { Cols: 0, Rows: 0 };
            var valid = cmd is { Cols: > 0 and <= ushort.MaxValue, Rows: > 0 and <= ushort.MaxValue };

            if (clear || valid) {
                lock (agent.SinksLock) {
                    agent.WebDims = clear ? null : new AgentInstance.Dim((ushort)cmd.Cols, (ushort)cmd.Rows);
                    ClampPtyLocked(agent);
                }

                // Announce the clamped size so every web viewer re-locks (and reconnect resends the
                // real size, not stale ones). Outside the lock, best-effort, fire-and-forget — same
                // as the local resize path in ApplyResizeClamp.
                _ = SafeSendDimsAsync(agent);
            }
        }

        return Task.CompletedTask;
    }

    static readonly TimeSpan ReRegisterRetryDelay = TimeSpan.FromMilliseconds(250);
    const           int      ReRegisterMaxAttempts = 3;

    /// <summary>
    /// Re-registers this daemon's live agents with the server (AgentRegistered +
    /// AgentStatusChanged) so per-session ownership is restored after a (re-)connect. Wired into
    /// <see cref="ServerConnection.ReRegisterAgentsHook"/> and awaited inside
    /// <see cref="ServerConnection.RegisterDaemon"/> BEFORE readiness is restored — so a
    /// permission invoke gated on <c>IsReady</c> can't fire before ownership recovery (AI-864).
    ///
    /// Each agent's re-registration is retried a bounded number of times before giving up, so a
    /// transient blip doesn't leave that agent's ownership unrestored while the daemon still
    /// flips ready (the qodo "ready despite reregister failures" gap). On final failure we log and
    /// move on rather than throw: one agent's persistent failure must NOT withhold readiness for
    /// the whole daemon (that would block every other agent's permissions and loop reconnects).
    /// The bounded ownership-retry in <see cref="ServerConnection.RequestPermissionAsync"/> is the
    /// final safety net for the residual case.
    /// </summary>
    async Task ReRegisterAgentsAsync() {
        // PrivateLocal agents are never registered with the server, so never re-register them.
        foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
            for (var attempt = 1; ; attempt++) {
                try {
                    await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath);
                    await _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId);

                    // Re-send the fixed PTY dims. The server stores them in memory, so a
                    // server restart (not just a daemon blip) wipes them — without this
                    // resend the read-only viewers never re-lock and the TUI garbles
                    // again exactly as before the fix (AI-884). Best-effort: its own
                    // catch keeps a dims-send failure from escaping to the retry handler
                    // (which would re-register the agent) or withholding readiness.
                    try {
                        await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
                    } catch (Exception ex) {
                        LogTerminalDimsSendFailed(ex, agent.Id);
                    }

                    // AI-842: do NOT replay the full output buffer here. The old
                    // replay re-sent the entire 2 MB ring on every reconnect, which
                    // the server appended to its own buffer and live-broadcast on
                    // top of the current screen — duplicated, and interleaved with
                    // the read loop's concurrent live sends, producing the garbled
                    // Terminal tab. The server retains its own per-agent buffer
                    // across a daemon rebind (it only clears on reconcile-to-Failed),
                    // so late-joining web clients still get history via the server's
                    // SubscribeToTerminal replay. Continuity of in-flight output is
                    // handled by TerminalOutputSender, which holds unsent chunks
                    // while the transport is down and flushes them, in order, once
                    // the connection is back.
                    break;
                } catch (Exception) when (attempt < ReRegisterMaxAttempts && !_shutdownCts.IsCancellationRequested) {
                    try {
                        await Task.Delay(ReRegisterRetryDelay, _shutdownCts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }
                } catch (Exception ex) {
                    LogReRegisterFailed(ex, agent.Id);

                    break;
                }
            }
        }
    }

    static readonly TimeSpan StartupTimeout     = TimeSpan.FromSeconds(90);
    static readonly TimeSpan MinSessionLifespan = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when the agent process exited before establishing a real interactive
    /// session. We require both that output was actually received (the read loop
    /// observed at least one chunk) AND that the gap between spawn and the last
    /// output is at least <see cref="MinSessionLifespan"/>. The
    /// <paramref name="hasReceivedOutput"/> guard prevents a no-output process
    /// from being misclassified when the <c>CreatedAt</c> and <c>LastOutputAt</c>
    /// field initializers happen to straddle a long pause.
    /// </summary>
    internal static bool IsStartupFailure(DateTime createdAt, DateTime lastOutputAt, bool hasReceivedOutput)
        => !hasReceivedOutput || lastOutputAt - createdAt < MinSessionLifespan;

    static readonly HashSet<string> ValidEffortLevels = ["low", "medium", "high", "max"];

    async Task RunHeartbeatLoopAsync(CancellationToken ct) {
        while (await _heartbeatTimer.WaitForNextTickAsync(ct)) {
            // PrivateLocal agents get no heartbeats and no stuck-Starting auto-stop (deny-all;
            // the local user is present and drives them directly).
            foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
                // Detect agents stuck in "Starting" with no output
                if (agent.Status                         == "Starting" &&
                    DateTime.UtcNow - agent.LastOutputAt > StartupTimeout) {
                    LogAgentStuck(agent.Id, (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds, agent.Process.Pid, agent.Process.HasExited);
                    _ = HandleStopAgent(agent.Id);

                    continue;
                }

                _ = _server.AppendAgentRunEventAsync(
                    agent.Id,
                    new AgentRunHeartbeat(agent.SessionId)
                );
            }
        }
    }

    async Task RunDaemonHeartbeatLoopAsync(CancellationToken ct) {
        var loop = new DaemonHeartbeatLoop(_server, PingDeadline, _logger);

        while (await _daemonHeartbeat.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but we
            // run as an unobserved background Task — guarding here keeps
            // the loop alive even if a future change accidentally lets an
            // exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Heartbeat tick faulted — continuing loop");
            }
        }
    }

    async Task CleanupAgentAsync(string agentId) {
        if (!_agents.TryRemove(agentId, out var agent)) {
            return;
        }

        // Wake any attached local clients blocked on the user's stdin so they can flush the
        // last output and send Exited (the agent is going away). The exit code is already
        // captured on agent.Process, so disposing it below doesn't lose it.
        try { await agent.ExitedCts.CancelAsync(); } catch { /* best-effort */ }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Process.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

        if (_launchers.TryGetValue(agent.Vendor, out var launcher)) {
            try { launcher.Cleanup(agent); } catch (Exception ex) { LogCleanupStepFailed(ex, "launcher.Cleanup", agentId); }
        }

        // Owned worktrees are daemon-created and safe to remove. A borrowed cwd is the
        // user's own checkout (local in-place launch) — NEVER delete it or its branch:
        // RemoveAsync would Directory.Delete / `git worktree remove --force` + `branch -D`.
        // This is the spec's top safety invariant.
        if (agent.Work == WorkLocation.OwnedWorktree) {
            try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }
        }

        // Skip server unregister during shutdown — _ct is cancelled and the call
        // would throw TaskCanceledException. The server detects the daemon
        // disconnection through SignalR's transport-level signals. Filtered
        // catch covers the residual race where shutdown fires mid-call.
        // PrivateLocal agents were never registered, so never unregister them (deny-all).
        if (!agent.IsPrivate && !_shutdownCts.IsCancellationRequested) {
            try { await _server.AgentUnregisteredAsync(agentId); } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) { } catch (Exception ex) {
                LogCleanupStepFailed(ex, "unregistering", agentId);
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _shutdownCts.CancelAsync();

        foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
            try {
                await agent.ReadCts.CancelAsync();
                await agent.Process.TerminateAsync(TimeSpan.FromSeconds(5));
            } catch {
                /* best-effort */
            }
        }

        foreach (var agentId in _agents.Keys.ToList()) {
            await CleanupAgentAsync(agentId);
        }

        _heartbeatTimer.Dispose();
        _daemonHeartbeat.Dispose();
    }

    /// <summary>
    /// Extracts readable text from the terminal output buffer by decoding UTF-8
    /// and stripping ANSI escape sequences. Returns the last ~500 chars to keep
    /// the error message reasonable for the UI snackbar.
    /// </summary>
    static string ExtractTerminalText(TerminalOutputBuffer buffer) {
        var chunks = buffer.GetAll();

        if (chunks.Count == 0) {
            return "";
        }

        var combined = new byte[chunks.Sum(c => c.Length)];
        var offset   = 0;

        foreach (var chunk in chunks) {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        var raw     = Encoding.UTF8.GetString(combined);
        var cleaned = StripAnsiRegex().Replace(raw, "").Trim();

        return cleaned.Length > 500 ? cleaned[^500..] : cleaned;
    }

    // ── LoggerMessage source-generated methods ────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching agent {AgentId} for {Repo} (effort={Effort}, model={Model})")]
    partial void LogLaunching(string agentId, string repo, string effort, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, claude={ClaudePath})")]
    partial void LogAgentSpawned(string agentId, int pid, string worktree, string claudePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} exited with code {ExitCode}")]
    partial void LogAgentExited(string agentId, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping agent {AgentId}")]
    partial void LogStopping(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download launch attachments for agent {AgentId} (continuing)")]
    partial void LogAttachmentDownloadFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading output for agent {AgentId}")]
    partial void LogOutputReadError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} failed during startup (exit code {ExitCode}): {Reason}")]
    partial void LogStartupFailed(string agentId, int? exitCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download attachment {Id}: {Status}")]
    partial void LogAttachmentNotFound(string id, System.Net.HttpStatusCode status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attachment filename would escape directory: {FileName}")]
    partial void LogAttachmentPathEscape(string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error downloading attachment {Id}")]
    partial void LogAttachmentError(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to re-register agent {AgentId}")]
    partial void LogReRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send terminal dimensions for agent {AgentId} (read-only viewers may render garbled until the next reconnect)")]
    partial void LogTerminalDimsSendFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating")]
    partial void LogAgentStuck(string agentId, double seconds, int pid, bool exited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error {Step} for agent {AgentId}")]
    partial void LogCleanupStepFailed(Exception ex, string step, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Launcher Prepare soft-failure for agent {AgentId} (continuing)")]
    partial void LogPrepareSoftFailure(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to launch agent {AgentId}")]
    partial void LogLaunchFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during cleanup of agent {AgentId}")]
    partial void LogCleanupError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error stopping agent {AgentId}")]
    partial void LogStopError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit failed for agent {AgentId}; falling back to SIGTERM")]
    partial void LogGracefulExitFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit window of {Seconds}s elapsed for agent {AgentId} without claude exiting; falling back to SIGTERM")]
    partial void LogGracefulExitTimedOut(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end session for agent {AgentId} (server may not record SessionEnded)")]
    partial void LogEndSessionFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register local agent {AgentId} with the server (continuing; terminal stays usable)")]
    partial void LogLocalRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EndAgentSession for agent {AgentId} did not complete within {Seconds}s; proceeding with cleanup while the retry continues in the background (server reconciles on daemon disconnect)")]
    partial void LogEndSessionTimedOut(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spawned what's-done generator for session {SessionId} (PID {Pid})")]
    partial void LogWhatsDoneSpawned(string sessionId, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to spawn what's-done generator for session {SessionId}")]
    partial void LogWhatsDoneSpawnFailed(Exception? ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh worktree for agent {AgentId} (source={Source}, target={Target})")]
    partial void LogRefreshWorktreeFailed(Exception ex, string agentId, string source, string target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();

    /// <summary>
    /// Method exposed for unit tests so they can drive <see cref="HandleLaunchAgent"/>
    /// without going through SignalR. Keeps the private handler private to everyone else.
    /// </summary>
    internal Task HandleLaunchAgentForTest(LaunchAgentCommand cmd) => HandleLaunchAgent(cmd);

    /// <summary>Test-only: register a pre-built agent so cleanup/lifecycle can be driven directly.</summary>
    internal void RegisterAgentForTest(AgentInstance agent) => _agents[agent.Id] = agent;

    /// <summary>Test-only entry point to the private cleanup path.</summary>
    internal Task CleanupAgentForTest(string agentId) => CleanupAgentAsync(agentId);

    /// <summary>Test-only: number of agents currently tracked (for awaiting cleanup).</summary>
    internal int ActiveAgentCountForTest => _agents.Count;

    /// <summary>Test-only entry point to the private stop handler (mirrors <see cref="HandleLaunchAgentForTest"/>).</summary>
    internal Task HandleStopAgentForTest(string agentId) => HandleStopAgent(agentId);

    internal Task RegisterAgentForTestAsync(AgentInstance agent) => RegisterAgentAsync(agent);
    internal Task ReRegisterAgentsForTestAsync() => ReRegisterAgentsAsync();
    internal void HandleResizeTerminalForTest(ResizeTerminalCommand cmd) => _ = HandleResizeTerminal(cmd);
    internal LocalPermissionBridge PermissionBridgeForTest => _permissionBridge;
}

/// <summary>
/// Stand-in <see cref="IPtyProcess"/> for failed-launch cleanup paths where we
/// need an <see cref="AgentInstance"/> to satisfy <c>launcher.Cleanup</c> but no
/// live PTY ever existed. All members are no-ops; the launcher only reads
/// <see cref="AgentInstance.Worktree"/> and <see cref="AgentInstance.McpConfigPath"/>.
/// </summary>
internal sealed class NoopPtyProcess : IPtyProcess {
    public static readonly NoopPtyProcess Instance = new();
    NoopPtyProcess() { }

    public int  Pid       => 0;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        yield break;
    }
#pragma warning restore CS1998

    public Task WriteAsync(string input) => Task.CompletedTask;
    public Task WriteAsync(byte[] data) => Task.CompletedTask;
    public void Resize(ushort     cols, ushort rows) { }
    public void SendInterrupt() { }
}
