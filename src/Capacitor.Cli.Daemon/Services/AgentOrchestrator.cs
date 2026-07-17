using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal record AgentInstance(
        string                  Id,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string                  RepoPath,
        string                  Vendor,
        IHostedAgentRuntime     Runtime,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    public string?              SessionId         { get; set; }
    public string               Status            { get; set; } = "Starting";
    public DateTime             CreatedAt         { get; init; } = DateTime.UtcNow;
    public DateTime             LastOutputAt      { get; set; } = DateTime.UtcNow;
    public bool                 HasReceivedOutput { get; set; }
    public TerminalOutputBuffer OutputBuffer      { get; } = new();

    /// <summary>AI-1313 Phase B (D2): the launch kind + (for a ReviewFlow launch) the flow identity,
    /// captured from <see cref="LaunchAgentCommand"/> at construction. Reported in
    /// <c>LiveAgents</c>/<c>DaemonStatusReport</c> so a restarted server can associate a surviving
    /// unassigned reviewer with its role. Defaults preserve pre-D2 behavior for any non-D2 launch path.</summary>
    public LaunchKind           Kind              { get; init; } = LaunchKind.Default;
    public string?              FlowRunId         { get; init; }
    public string?              FlowRole          { get; init; }

    /// <summary>Temp MCP config path written for hosted PR reviews; deleted on cleanup.</summary>
    public string? McpConfigPath { get; set; }

    /// <summary>The per-reviewer LocalPermissionBridge token URL minted for an unattended review-flow
    /// launch (null otherwise). Revoked on cleanup so the auto-approve path dies with the reviewer.</summary>
    public string? ReviewerBridgeToken { get; init; }

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

    /// <summary>
    /// The live ACP transcript forwarder for this agent, set once
    /// <see cref="AgentOrchestrator.HandleLaunchAgent"/>'s post-registration bind
    /// (<c>AcpSessionStarted</c>) succeeds and the forwarder is constructed. <see langword="null"/>
    /// for every PTY agent (claude/codex — <see cref="HostedRuntimeStart.Transcript"/> is null for
    /// them) and for an ACP agent whose initial bind failed (nothing to drain in that case — see
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/>). Read by
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> to run the bounded final-drain before
    /// ending the session.
    /// </summary>
    public AcpForwarderHandle? AcpForwarder { get; set; }

    /// <summary>
    /// Per-agent/per-setup <see cref="CancellationTokenSource"/>, linked to the daemon's shutdown
    /// token, created in <see cref="AgentOrchestrator.HandleLaunchAgent"/> BEFORE the fire-and-forget
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/> call — so it exists immediately,
    /// independent of whether the bind ever resolves. Both the bind/setup task and (once started) the
    /// forwarder's run task use ITS token rather than the raw daemon-wide shutdown token, so
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can cancel just this agent's ACP work
    /// (on drain-timeout, and unconditionally at finalize) without touching any other agent or the
    /// daemon's own shutdown gate. <see langword="null"/> for every PTY agent (claude/codex) and set
    /// exactly once, at launch, for every ACP-capable runtime — never re-created for the same agent.
    /// </summary>
    public CancellationTokenSource? AcpCts { get; set; }
}

/// <summary>
/// Pairs a started <see cref="AcpTranscriptForwarder"/> with its fire-and-forget run task
/// (<see cref="AgentOrchestrator.ForwardAcpTranscriptAsync"/>'s return
/// value) so <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can await the SAME task (bounded)
/// at teardown without re-deriving or re-wrapping it.
/// </summary>
internal sealed record AcpForwarderHandle(AcpTranscriptForwarder Forwarder, Task RunTask);

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

    // AI-1313 Phase B (D4): durable PID records + this daemon's logical identity/epoch for
    // crash-survivor reaping. Initialized in the ctor from config.
    AgentPidRecordStore? _pidRecords;
    string               _daemonId    = "";
    string               _daemonEpoch = "";
    readonly DaemonConfig                                      _config;
    readonly ServerConnection                                  _server;
    readonly WorktreeManager                                   _worktreeManager;
    readonly RepoMatcher                                       _repoMatcher;
    readonly IPtyProcessFactory                                _ptyFactory;
    readonly IHttpClientFactory                                _httpClientFactory;
    readonly LocalPermissionBridge                             _permissionBridge;
    readonly IReadOnlyDictionary<string, IHostedAgentLauncher> _launchers;
    readonly IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> _runtimeFactories;
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

    // AI-992: proactively refresh the active profile's auth token ahead of expiry so a
    // continuously-running daemon keeps a WorkOS sliding-inactivity session alive (up to its
    // absolute lifetime) rather than forcing a `kcap login` after an idle period. The tick is
    // cheap (a token-file read + expiry compare) and only calls the refresh endpoint when the
    // token is within ProactiveRefreshWindow of expiry; TokenRefreshLoop further rate-limits
    // attempts to at most one per ProactiveRefreshMinInterval, so refresh traffic stays bounded
    // even for a failing refresh or a short-lived token that keeps re-entering the window.
    readonly PeriodicTimer _tokenRefresh = new(TimeSpan.FromSeconds(60));

    // AI-1357 Task 12: periodic sweep of the cross-vendor lifecycle + transcript spools. Covers
    // backlogs left behind by vendors whose session-end never fires another `kcap` hook process
    // (Kiro/OpenCode watcher-owned session-end, Antigravity/Codex-desktop GUI idle/parent-exit) —
    // see SpoolDrainLoop's doc comment. 60s mirrors the reaper-style cadence of the other timers;
    // the drain's own per-tick budget keeps a slow/unreachable server from stalling the daemon.
    readonly PeriodicTimer _spoolDrain = new(TimeSpan.FromSeconds(60));

    // Refresh once the token is within this much of its expiry. Comfortably above the 60 s tick
    // so the window is never stepped over.
    static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromMinutes(5);

    // Hit the refresh endpoint at most once per this interval (see TokenRefreshLoop). Small
    // enough that a healthy token issued with a short lifetime is still renewed before it
    // lapses during idle; large enough that a dead/rotated refresh token isn't re-hit every
    // tick.
    static readonly TimeSpan ProactiveRefreshMinInterval = TimeSpan.FromMinutes(5);

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
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories,
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
        _runtimeFactories  = runtimeFactories;
        _logger            = logger;

        // AI-1313 Phase B (D4): per-daemon PID-record store + this daemon's logical id + boot epoch.
        // Records live under "{stateDir}/{name}/agents" so they are unambiguously THIS daemon's own
        // (the startup reap only touches its own leftovers). DaemonId is a stable per-name identity;
        // DaemonEpoch is fresh per boot so the env-marker scan can tell a prior incarnation's
        // survivors from the current incarnation's live children.
        var recordRoot = Path.Combine(
            config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));
        _pidRecords  = new AgentPidRecordStore(recordRoot, logger);
        _daemonId    = ComputeDaemonId(config.Name);
        _daemonEpoch = config.DaemonEpoch ?? Guid.NewGuid().ToString("N");

        // Wire up server commands
        _server.OnLaunchAgent            += HandleLaunchAgent;
        _server.OnStopAgent              += HandleStopAgent;
        _server.OnSendInput              += HandleSendInput;
        _server.OnSendSpecialKey         += HandleSendSpecialKey;
        _server.OnResizeTerminal         += HandleResizeTerminal;
        _server.ReRegisterAgentsHook          =  ReRegisterAgentsAsync;
        _server.FindRepoForRemoteHandler      =  HandleFindRepoForRemote;
        _server.ProbeBorrowSourceHandler      =  HandleProbeBorrowSource;

        _server.GetLiveAgentIds = () => [
            .. _agents
                .Where(kvp => (kvp.Value.Status is "Starting" or "Running") && !kvp.Value.IsPrivate)
                .Select(kvp => kvp.Key)
        ];

        // AI-1313 Phase B (D2): richer live-agent metadata (kind + flow identity) alongside the ids.
        _server.GetLiveAgents = () => [.. BuildLiveAgents()];

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunTokenRefreshLoopAsync(_shutdownCts.Token);
        _ = RunSpoolDrainLoopAsync(_shutdownCts.Token);
        _ = RunDaemonStatusReportLoopAsync(_shutdownCts.Token); // AI-1313 Phase B (D2): periodic self-report
    }

    internal int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

    /// <summary>AI-1313 Phase B (D3): clock seam so the reviewer-TTL heartbeat check is testable with a
    /// fixed time. Production uses the real UTC clock.</summary>
    internal Func<DateTime> ClockUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>AI-1313 Phase B (D3): the ReviewFlow agents the heartbeat should reap now — past
    /// <see cref="DaemonConfig.ReviewerMaxLifetime"/> (reason <c>reviewer_ttl_expired</c>) or
    /// <see cref="DaemonConfig.ReviewerIdleTimeout"/> (reason <c>reviewer_idle_expired</c>). Only
    /// Running ReviewFlow agents; a <see cref="TimeSpan.Zero"/> bound disables it; interactive agents
    /// are never returned. Pure (no side effects) so the heartbeat and tests share one decision.</summary>
    internal IReadOnlyList<(string Id, string Reason)> FindReviewersToReap() {
        var now    = ClockUtc();
        var result = new List<(string, string)>();

        foreach (var a in _agents.Values) {
            if (a.Kind != LaunchKind.ReviewFlow || a.Status != "Running") continue;

            if (_config.ReviewerMaxLifetime > TimeSpan.Zero && now - a.CreatedAt > _config.ReviewerMaxLifetime)
                result.Add((a.Id, "reviewer_ttl_expired"));
            else if (_config.ReviewerIdleTimeout > TimeSpan.Zero && now - a.LastOutputAt > _config.ReviewerIdleTimeout)
                result.Add((a.Id, "reviewer_idle_expired"));
        }

        return result;
    }

    /// <summary>AI-1313 Phase B (D2): the daemon's self-report snapshot — its authoritative
    /// <see cref="ActiveCount"/> plus the live-agent metadata (and, once D4/Task 8 lands, the
    /// kill-quarantine). Pure; the send loop + tests share it.</summary>
    internal DaemonStatusReport BuildStatusReport() =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()]);

    /// <summary>AI-1313 Phase B: the kill-quarantine snapshot for the status report. Empty until D4
    /// Task 8 wires the real <c>AgentKillQuarantine</c>; kept as a seam so BuildStatusReport is stable.</summary>
    internal IReadOnlyList<QuarantinedAgentInfo> QuarantineSnapshot() => [];

    /// <summary>AI-1313 Phase B (D4): this daemon's stable logical id = a hash of its name, written
    /// into each child's <c>KCAP_DAEMON_ID</c> marker. Per-name so a different daemon under the same
    /// user is never mistaken for ours by the env-marker scan.</summary>
    static string ComputeDaemonId(string name) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name ?? "")))
        [..16].ToLowerInvariant();

    /// <summary>AI-1313 Phase B (D4 §6.4(2)): write the durable PID record for a just-spawned agent
    /// (best-effort — a lost record falls to the env-marker scan backstop; D4/Task 8 hardens the
    /// write-failure path to fail-closed). Captures the EXACT start-identity for the pid.</summary>
    void WritePidRecordBestEffort(AgentInstance agent, int pid) {
        if (_pidRecords is null) return;

        var identity = ProcessIdentity.Capture(pid);
        if (identity is null) return; // unidentifiable pid → skip (env-marker scan backstops it)

        try {
            _pidRecords.Write(new AgentPidRecord(
                agent.Id, pid, identity, agent.Kind.ToString(), agent.Vendor,
                agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to write PID record for agent {AgentId}", agent.Id);
        }
    }

    /// <summary>Delete an agent's PID record after its death is confirmed (teardown / confirmed reap).</summary>
    void DeletePidRecord(string agentId) => _pidRecords?.Delete(agentId);

    /// <summary>Test seams (this daemon's PID-record store) so a unit test can seed/inspect records
    /// without a real launch. Never used in production.</summary>
    internal void WritePidRecordForTest(AgentPidRecord record)     => _pidRecords?.Write(record);
    internal IReadOnlyList<AgentPidRecord> PidRecordsForTest()      => _pidRecords?.ReadAll() ?? [];
    internal string DaemonIdForTest                                 => _daemonId;
    internal string DaemonEpochForTest                             => _daemonEpoch;

    /// <summary>AI-1313 Phase B (D4 §6.4(3) StopAgent fallback): the caller had no in-memory agent for
    /// this id — consult the PID record and, if a live process still matches its EXACT identity (and,
    /// on Unix, carries the expected <c>KCAP_AGENT_ID</c> env — ambiguity spares), reap it by identity
    /// and delete the record on confirmed death. This makes the server's registry-independent S2 stop
    /// effective even against a NEW daemon incarnation that never knew the agent in memory.</summary>
    async Task<bool> TryStopByPidRecordAsync(string agentId) {
        if (_pidRecords is null) return false;

        var record = _pidRecords.ReadAll().FirstOrDefault(r => r.AgentId == agentId);
        if (record.AgentId != agentId) return false; // no record

        var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, _logger, _shutdownCts.Token);
        if (confirmedGone) _pidRecords.Delete(agentId); // delete ONLY on confirmed death (spec §6.4(2))

        return confirmedGone;
    }

    /// <summary>AI-1313 Phase B (D2): build + send one status report, one-way, swallowing errors (an
    /// old server has no handler; a transient send failure must not touch the agent loops).</summary>
    internal async Task SendDaemonStatusReportOnceAsync() {
        try { await _server.DaemonStatusReportAsync(BuildStatusReport()); }
        catch (Exception ex) { _logger.LogDebug(ex, "DaemonStatusReport send failed — ignoring"); }
    }

    async Task RunDaemonStatusReportLoopAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct)) {
            try { await SendDaemonStatusReportOnceAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "DaemonStatusReport loop tick faulted — continuing"); }
        }
    }

    /// <summary>AI-1313 Phase B (D2): one <see cref="LiveAgentInfo"/> per currently-live (Starting or
    /// Running), non-private agent, carrying its kind + flow identity. Mirrors the
    /// <see cref="ServerConnection.GetLiveAgentIds"/> filter (private-local agents excluded).</summary>
    internal IReadOnlyList<LiveAgentInfo> BuildLiveAgents() =>
        [.. _agents.Values
            .Where(a => a.Status is "Starting" or "Running" && !a.IsPrivate)
            .Select(a => new LiveAgentInfo(a.Id, a.Kind.ToString(), a.CreatedAt, a.FlowRunId, a.FlowRole))];

    /// <summary>AI-1313 Phase B: test-only seam — insert a minimal <see cref="AgentInstance"/> (Noop
    /// PTY runtime, no real process/worktree) so unit tests can exercise <see cref="BuildLiveAgents"/>
    /// / status-report / reviewer-TTL logic without a live launch. Never called in production.</summary>
    internal AgentInstance SeedAgentForTest(
            string id, LaunchKind kind = LaunchKind.Default, string status = "Running",
            string? flowRunId = null, string? flowRole = null,
            DateTime? createdAt = null, DateTime? lastOutputAt = null, bool isPrivate = false) {
        var agent = new AgentInstance(
            id, null, "default", null, "/repo", "codex",
            new PtyHostedAgentRuntime("codex", NoopPtyProcess.Instance),
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Kind = kind, FlowRunId = flowRunId, FlowRole = flowRole, IsPrivate = isPrivate,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        agent.Status = status;
        if (lastOutputAt is { } lo) agent.LastOutputAt = lo;
        _agents[id] = agent;
        return agent;
    }

    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var agentId       = cmd.AgentId;
        var prompt        = cmd.Prompt;
        var model         = cmd.Model;
        var effort        = cmd.Effort;
        var repoPath      = cmd.RepoPath;
        var tools         = cmd.Tools;
        var attachmentIds = cmd.AttachmentIds;
        var isReview      = cmd.Kind == LaunchKind.Review;
        var isReviewFlow  = cmd.Kind == LaunchKind.ReviewFlow;

        // Guard for a null/blank vendor before the dictionary lookup: LaunchAgentCommand crosses
        // the SignalR boundary where the non-null annotation isn't enforced, and Dictionary
        // .TryGetValue(null) throws ArgumentNullException — which SafeInvoke would swallow, dropping
        // the launch with no LaunchFailed reaching the server. (The removed vendor allowlist used to
        // absorb this incidentally.)
        if (string.IsNullOrWhiteSpace(cmd.Vendor) || !_runtimeFactories.TryGetValue(cmd.Vendor, out var runtimeFactory)) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");

            return;
        }

        // AI-1124: fail an unattended (review-flow) launch fast when the selected vendor's
        // runtime can't run unattended — before creating a worktree, so there's nothing to
        // clean up. Both shipped PTY launchers support it; this guards future vendors (and the
        // ACP/Cursor runtime, which does not).
        if (UnattendedLaunchPolicy.RejectionReason(cmd.Vendor, runtimeFactory.SupportsUnattended, isReviewFlow) is { } unattendedRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, unattendedRejection);

            return;
        }

        WorktreeInfo? worktree      = null;
        string?       mcpConfigPath = null;

        // Declared OUTSIDE the try so it is in scope in the catch blocks below: the failed-launch
        // cleanup must consult it to decide whether the worktree is ours to remove. A borrowed cwd
        // is the user's real checkout — never removed on any path (spec's top safety invariant).
        var work = cmd.Borrowed ? WorkLocation.BorrowedCwd : WorkLocation.OwnedWorktree;

        // The per-reviewer bridge token URL (if this is an unattended review-flow launch), hoisted to
        // method scope so the failure catch can revoke it when no AgentInstance was created to carry it.
        string? reviewerToken = null;

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

            if (cmd.Borrowed) {
                // Defense-in-depth re-authorization: the server already probed this cwd, but the
                // daemon NEVER borrows a path just because the server said so (TOCTOU-safe). The
                // reason is surfaced verbatim to the server via the catch → LaunchFailedAsync below;
                // Phase B (server) keys off the `borrow_auth_failed:` prefix.
                var auth = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(cmd.BorrowCwd ?? "");

                if (!auth.Allowed) {
                    throw new InvalidOperationException($"borrow_auth_failed: {auth.Reason}");
                }

                // Run IN the user's real (canonicalized) checkout. Deliberately SKIP CreateAsync, any
                // base fetch / `git worktree add`, the launch-time mirror, and the attachment
                // download-into-cwd — every one of those would mutate the user's own tree.
                worktree = WorktreeInfo.Borrowed(auth.CanonicalCwd!);
            } else {
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
            }

            // An unattended review-flow reviewer must auto-approve its kcap tool calls (no human is
            // present): mint a dedicated bridge token bound to the launch's read-only kcap allowlist
            // and hand the reviewer that token's URL as KCAP_DAEMON_URL. An invalid allowlist fails
            // the launch FAST rather than falling back to a prompt that would hang.
            var daemonBridgeUrl    = _permissionBridge.BaseUrl;
            var effectiveAllowlist = cmd.McpAllowlist;

            // Only Codex reviewers get a token: their MCP config-lock is what makes a bare tool name
            // provably a bound kcap tool (see LocalPermissionBridge.IsReviewerToolAllowed). Claude
            // reviewers run via bypassPermissions with only the flow-result channel, so they need
            // none. Also guarded on the bridge listening (BaseUrl != null) — a graceful no-op otherwise.
            if (isReviewFlow && daemonBridgeUrl is not null
                && string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)) {
                if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(cmd.McpAllowlist, out var reviewerServers, out var rejected)) {
                    await _server.LaunchFailedAsync(agentId,
                        $"Review-flow reviewer MCP allowlist contains a server that is not auto-approvable: '{rejected}'.");

                    if (work == WorkLocation.OwnedWorktree) {
                        try { await WorktreeManager.RemoveAsync(worktree); } catch { /* best-effort */ }
                    }

                    return;
                }

                daemonBridgeUrl    = _permissionBridge.RegisterReviewerToken(reviewerServers);
                reviewerToken      = daemonBridgeUrl;   // the URL doubles as the revoke handle
                effectiveAllowlist = reviewerServers;   // single source: the set the launcher materializes
            }

            var runtimeCtx = new RuntimeStartContext(
                AgentId: agentId,
                Vendor: cmd.Vendor,
                SourceRepoPath: repoPath,
                Worktree: worktree,
                Prompt: prompt,
                Model: model,
                Effort: effort,
                Tools: tools,
                IsReview: isReview,
                IsReviewFlow: isReviewFlow,
                Review: cmd.Review,
                Cols: HostedPtyCols,
                Rows: HostedPtyRows,
                ServerUrl: _config.ServerUrl,
                DaemonBridgeUrl: daemonBridgeUrl,
                CapacitorPath: _config.CapacitorPath,
                McpAllowlist: effectiveAllowlist,
                Work: work
            );

            HostedRuntimeStart start;

            try {
                start = await runtimeFactory.StartAsync(runtimeCtx, _shutdownCts.Token);
            } catch (CodexHooksNotInstalledException ex) {
                await _server.LaunchFailedAsync(agentId, ex.Message);

                // No AgentInstance was created, so CleanupAgentAsync won't run — revoke the reviewer
                // token here (if we minted one) so it doesn't leak into the live-token set.
                if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

                // Still need to clean up the worktree before returning — but ONLY if we own it.
                // A borrowed cwd is the user's real checkout; removing it here would `git worktree
                // remove` the user's tree (spec's top safety invariant; mirrors CleanupAgentAsync).
                if (work == WorkLocation.OwnedWorktree) {
                    try { await WorktreeManager.RemoveAsync(worktree); } catch {
                        /* best-effort */
                    }
                }

                return;
            }

            mcpConfigPath = start.McpConfigPath;
            var runtime = start.Runtime;

            LogAgentSpawned(agentId, runtime.Pid, worktree.Path, runtimeFactory.Vendor);

            var cts = new CancellationTokenSource();

            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, cmd.Vendor, runtime, worktree, cts) {
                McpConfigPath       = mcpConfigPath,
                CurrentCols         = HostedPtyCols,
                CurrentRows         = HostedPtyRows,
                Work                = work,
                ReviewerBridgeToken = reviewerToken,
                Kind                = cmd.Kind,       // AI-1313 Phase B (D2): flow identity + kind for LiveAgents/status report
                FlowRunId           = cmd.FlowRunId,
                FlowRole            = cmd.FlowRole
            };
            _agents[agentId] = agent;

            // AI-1313 Phase B (D4 §6.4(2)): write the durable PID record immediately after the process
            // exists (before registration) so a daemon crash right after this leaves a reapable record.
            WritePidRecordBestEffort(agent, runtime.Pid);

            await RegisterAgentAsync(agent);

            // A runtime with no terminal output (ACP/cursor) has no output-chunk signal to flip
            // Starting→Running on — ReadAgentOutputAsync's read loop never yields a byte for such
            // a runtime, so without this the agent would sit in "Starting" until the heartbeat's
            // StartupTimeout auto-stops it as stuck (AI-684 Fix B/E). Flip to Running immediately:
            // the runtime factory's StartAsync already completed the ACP initialize/session-new
            // handshake by the time we get here, so the session really is established. PTY
            // runtimes are unaffected — they keep the existing on-first-chunk flip in
            // ReadAgentOutputAsync unchanged.
            if (!runtime.EmitsTerminalOutput) {
                agent.Status            = "Running";
                agent.HasReceivedOutput = true;
                if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
            }

            // Bind + start live transcript forwarding for any runtime that exposes an ACP
            // transcript source (Cursor today; null for every PTY runtime — no branch taken for
            // claude/codex). Fire-and-forget from here, exactly like ReadAgentOutputAsync below: the
            // bind call is IsReady-gated and can block across a reconnect outage (ConnectionRetry),
            // and HandleLaunchAgent must never stall on it — a stalled launch would queue every OTHER
            // inbound hub command behind it on this daemon's single SignalR connection.
            // StartAcpForwardingAsync itself still enforces the load-bearing ordering (bind strictly
            // after RegisterAgentAsync above, strictly before any AcpSessionEvents) by awaiting the
            // bind before constructing the forwarder.
            if (start.Transcript is { } transcript) {
                // Create + store the per-agent CTS BEFORE firing the setup task, so it exists for
                // FinalizeAgentRunAsync to cancel even if the agent finalizes before the bind below
                // ever resolves (see AgentInstance.AcpCts).
                var acpCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                agent.AcpCts = acpCts;
                _ = StartAcpForwardingAsync(agent, transcript, cmd.Vendor, acpCts);
            }

            // Start reading output
            _ = ReadAgentOutputAsync(agent);

            // Report the resolved model so the server can display the real model the agent
            // is running (the dispatched `model` may be the "default" no-override sentinel,
            // in which case Codex picks the model from ~/.codex/config.toml). The hub contract
            // (ReportAgentResolvedModel) is Codex-only and the resolution via CodexConfigToml is
            // Codex-specific, so gate the call on vendor — Claude/other agents never call the hub.
            // Best-effort: never let a report failure break the launch.
            if (string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)) {
                ReportResolvedModel(agentId, cmd.Vendor, model);
            }
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            // If a reviewer token was minted before the failure and no AgentInstance was created to
            // own it, revoke it here so it can't linger in the bridge's live-token set.
            if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

            // Only tear down a worktree we OWN. A borrowed cwd is the user's real checkout — never
            // remove it, its branch, or its Claude project symlink on a failed launch (spec's top
            // safety invariant; mirrors the normal-stop guard in CleanupAgentAsync). For a borrowed
            // launch there is nothing daemon-created to clean up anyway (no CreateAsync, no mirror,
            // no attachments), and StartAsync throwing means mcpConfigPath was never assigned.
            if (worktree != null && work == WorkLocation.OwnedWorktree) {
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
                            new PtyHostedAgentRuntime(cmd.Vendor, NoopPtyProcess.Instance),
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

    /// <summary>
    /// Best-effort: report the model the agent will actually run to the server so the UI can
    /// show the real model instead of the dispatched value. Codex-only — the hub contract
    /// (ReportAgentResolvedModel) and the config resolution are Codex-specific, so the caller
    /// gates this on <c>vendor == "codex"</c>. The dispatched <paramref name="model"/> may be
    /// the "default" no-override sentinel (or empty), in which case Codex resolves the model from
    /// <c>~/.codex/config.toml</c> — we resolve the same value here. Never throws: a resolve/report
    /// failure must not break launch.
    /// </summary>
    void ReportResolvedModel(string agentId, string vendor, string model) {
        try {
            var isDefault = string.IsNullOrEmpty(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);

            var resolved = isDefault && string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase)
                ? CodexResolvedModel(model)
                : model;

            if (string.IsNullOrEmpty(resolved)) return;

            _ = _server.ReportAgentResolvedModelAsync(agentId, resolved);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Reads the top-level <c>model = "…"</c> from <c>~/.codex/config.toml</c> (honouring
    /// <c>CODEX_HOME</c> via <see cref="CodexPaths"/>); falls back to <paramref name="fallback"/>
    /// when the file is missing/unreadable or has no top-level model key.
    /// </summary>
    static string CodexResolvedModel(string fallback) {
        var fromConfig = CodexConfigToml.ReadTopLevelModel();

        return string.IsNullOrWhiteSpace(fromConfig) ? fallback : fromConfig;
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
            await foreach (var data in agent.Runtime.ReadOutputAsync(agent.ReadCts.Token)) {
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
            await agent.Runtime.WaitForExitAsync(TimeSpan.FromSeconds(5));

            var exitCode = agent.Runtime.ExitCode;

            var status = agent.Runtime.HasExited
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
                //
                // This whole heuristic is output-stream-centric and only applies to a runtime
                // whose ReadOutputAsync yields real terminal bytes (PTY). A no-terminal runtime
                // (ACP/cursor) never has output to key off, so gate the check on
                // EmitsTerminalOutput — such a runtime is Completed/Failed purely by exit code
                // (AI-684 Fix B/E), never misclassified as a startup failure just for having
                // produced no output.
                if (agent.Runtime.EmitsTerminalOutput && IsStartupFailure(agent.CreatedAt, agent.LastOutputAt, agent.HasReceivedOutput)) {
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

            // For an ACP agent with a live forwarder, give the transcript a bounded chance to drain
            // BEFORE ending the session — this must NEVER pin shutdown (see
            // FinalDrainAcpTranscriptAsync's remarks); it always returns within AcpFinalDrainBudget
            // regardless of outcome. PTY agents have no AcpForwarder and take none of this path — the
            // runtime is disposed exactly where it always was, inside CleanupAgentAsync below.
            if (agent.AcpForwarder is { } acpForwarder) {
                await FinalDrainAcpTranscriptAsync(agent, acpForwarder);
            }

            // Cancel the per-agent ACP CTS unconditionally here — not only inside
            // FinalDrainAcpTranscriptAsync's own timeout branch above — so a bind/setup task that's
            // STILL in flight (the agent exited before its bind ever completed, so AcpForwarder is
            // still null and the drain step above never ran at all) observes cancellation now and can
            // abort at its liveness check (StartAcpForwardingAsync) before it ever registers a
            // binding for an agent that is finalizing right now. Runs BEFORE EndAgentSessionAsync so
            // any forwarder is fully stopped before the binding goes terminal server-side (the same
            // ordering the drain above already protects). Idempotent/harmless if already cancelled by
            // the drain step.
            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort */ }
            }

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

            // Drop the reconnect re-bind registration now that EndAgentSessionAsync above has
            // (best-effort) made this binding terminal server-side — a later reconnect must not try
            // to re-bind a session that's already ended. Unconditional — NOT gated on AcpForwarder
            // having ever been set — so a binding a late/racing setup managed to register despite the
            // cancellation above (StartAcpForwardingAsync's liveness check narrows but can't fully
            // eliminate that window) still gets cleaned up here; UnregisterAcpBinding is a no-op when
            // nothing was ever registered, so this call is always safe to make unconditionally.
            _server.UnregisterAcpBinding(agent.Id);

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

    internal async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            // AI-1313 Phase B (D4 §6.4(3)): no in-memory agent — this may be a survivor of a PRIOR
            // daemon incarnation the server is still trying to stop (S2). Fall back to the PID record:
            // reap by exact identity if a matching live process is still there.
            await TryStopByPidRecordAsync(agentId);
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
                await agent.Runtime.RequestGracefulStopAsync();
                await agent.Runtime.WaitForExitAsync(GracefulExitWait);
            } catch (Exception ex) {
                LogGracefulExitFailed(ex, agentId);
            }

            // PTY WaitForExitAsync(timeout) returns silently when the timeout elapses,
            // so a graceful-exit *timeout* doesn't throw. Check HasExited explicitly
            // so we can tell from logs whether the graceful path is actually working
            // in production or if claude is consistently being SIGTERMed instead.
            if (!agent.Runtime.HasExited) {
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
            await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10));
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

        // AI-30: PtyHostedAgentRuntime.SendUserInputAsync delivers this as a bracketed paste so
        // the CLI's TUI treats it as one pasted block and the following Enter is an unambiguous
        // submit keypress (see its doc comment for why a naive text-then-CR write mishandles
        // large multi-line input). The ACP runtime sends a structured session/prompt instead, so
        // this call is the single vendor-agnostic entry point for both.
        await agent.Runtime.SendUserInputAsync(message);
    }

    async Task HandleSendSpecialKey(string agentId, string key) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        if (agent.IsPrivate) return; // server-origin key ignored for private agents

        await agent.Runtime.SendSpecialKeyAsync(key);
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
    /// Handles the server's <c>ProbeBorrowSource</c> client-result invocation (AI-1207 Phase A, task
    /// A3): "can you borrow this path?". Delegates the actual policy (allowlist, git-root resolution,
    /// symlink canonicalization) to <see cref="BorrowAuthorizer"/> — constructed fresh over the
    /// daemon's current <see cref="DaemonConfig"/> so a config reload is picked up on the next probe —
    /// and maps its <see cref="BorrowAuthResult"/> onto the wire-facing <see cref="BorrowProbeResult"/>.
    /// </summary>
    async Task<BorrowProbeResult> HandleProbeBorrowSource(string path) {
        var result = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(path);

        return new BorrowProbeResult(result.Allowed, result.CanonicalCwd, result.CanonicalGitRoot, result.Reason);
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

    /// <summary>
    /// Binds the ACP canonical session to <paramref name="agent"/> (<c>AcpSessionStarted</c>) —
    /// this call MUST run after <see cref="RegisterAgentAsync"/> has already registered the agent (the server rejects a
    /// bind for an unregistered agent) and strictly before any transcript event reaches the server;
    /// callers (<see cref="HandleLaunchAgent"/>) enforce the first half by only calling this after
    /// <c>await RegisterAgentAsync(agent)</c>, and this method enforces the second half itself by
    /// awaiting the bind before ever constructing the forwarder. Once bound, registers the binding
    /// for reconnect re-bind (<see cref="ServerConnection.RegisterAcpBinding"/>), builds the
    /// synthesized <c>SessionStarted@Seq0</c> envelope (<see cref="AcpEventTranslator.BuildSessionStarted"/>),
    /// and starts <see cref="ForwardAcpTranscriptAsync"/> as background work — the resulting task is
    /// kept on <see cref="AgentInstance.AcpForwarder"/> so <see cref="FinalizeAgentRunAsync"/> can
    /// coordinate the bounded final-drain at teardown.
    ///
    /// Best-effort: any failure in the bind/setup step is logged and swallowed, never propagated to
    /// the caller. By the time this runs, <paramref name="agent"/> is already registered with the
    /// server and its ACP process is already live — letting a transcript-plumbing failure escape
    /// into <see cref="HandleLaunchAgent"/>'s outer catch would incorrectly route it through the
    /// failed-launch cleanup path (worktree removal) against an agent that is actually running.
    /// Degrades to "no live transcript for this session" rather than failing the launch.
    ///
    /// <b>Bind-vs-finalize stale-binding race:</b>
    /// <paramref name="acpCts"/> is <paramref name="agent"/>'s <see cref="AgentInstance.AcpCts"/>,
    /// created by the caller BEFORE this task was fired. Its token — not the raw daemon shutdown
    /// token — gates the bind call below AND (once built) the forwarder's run task, so
    /// <see cref="FinalizeAgentRunAsync"/> can cancel just this setup/forwarder. The bind call can
    /// block for the length of a reconnect outage (<c>ConnectionRetry</c>); if the agent's whole
    /// lifecycle finalizes while it's still in flight, a LATE successful bind must not register a
    /// binding for what is by then a dead agent (it would leak into <c>_acpBindings</c> and be
    /// replayed on every future reconnect with nothing left to ever drain it) — the liveness check
    /// immediately below the bind await closes that race.
    /// </summary>
    async Task StartAcpForwardingAsync(AgentInstance agent, IAcpTranscriptSource transcript, string vendor, CancellationTokenSource acpCts) {
        try {
            await _server.AcpSessionStartedAsync(
                agent.Id,
                vendor,
                transcript.AcpSessionId,
                transcript.Cwd,
                transcript.ResolvedModel,
                null, // metadata: no wire-contract fields required for the prototype
                acpCts.Token
            );

            // Liveness check: the await above can span a reconnect outage.
            // If finalize already ran (cancelling acpCts and/or removing the agent from _agents)
            // while we were waiting, abort here — do not register a binding, build a forwarder, or
            // start it for an agent that's finalizing or already gone.
            if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
                LogAcpBindAbortedAgentGone(agent.Id);

                return;
            }

            _server.RegisterAcpBinding(
                agent.Id,
                new AcpBindInfo(vendor, transcript.AcpSessionId, transcript.Cwd, transcript.ResolvedModel)
            );

            // Post-register re-check (TOCTOU): finalize can run between the liveness check above and
            // this register, having already cancelled/unregistered+cleaned up the agent — leaving the
            // binding we just registered stale (replayed on reconnect for a dead agent). Undo it. The
            // finalizer's own unconditional UnregisterAcpBinding covers the mirror case (finalize after
            // this point); UnregisterAcpBinding is idempotent so a double-remove is harmless.
            if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
                _server.UnregisterAcpBinding(agent.Id);
                LogAcpBindAbortedAgentGone(agent.Id);

                return;
            }

            var sessionStarted = AcpEventTranslator.BuildSessionStarted(
                seq: 0,
                DateTimeOffset.UtcNow.ToString("O"),
                cwd: transcript.Cwd,
                model: transcript.ResolvedModel,
                rawSessionId: transcript.AcpSessionId
            );

            var forwarder = new AcpTranscriptForwarder(
                send: (batch, ct) => _server.SendAcpEventsAsync(agent.Id, transcript.AcpSessionId, batch, ct),
                initialEnvelope: sessionStarted,
                envelopes: transcript.Envelopes,
                logger: _logger
            );

            var runTask = ForwardAcpTranscriptAsync(agent, forwarder, acpCts.Token);
            agent.AcpForwarder = new AcpForwarderHandle(forwarder, runTask);
        } catch (Exception ex) {
            LogAcpBindFailed(ex, agent.Id);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="AcpTranscriptForwarder.RunAsync"/> — a forwarder fault must NEVER
    /// crash the agent or the daemon. <see cref="AcpTranscriptForwarder.RunAsync"/> already swallows
    /// its own cancellation and retries indefinitely on a send failure, but this wrapper is the outer
    /// safety net for anything else that could still escape it (e.g. the transcript channel itself
    /// faulting from a translator bug upstream) — logged, never rethrown, so the returned task always
    /// completes successfully and <see cref="FinalizeAgentRunAsync"/> can safely await it.
    /// </summary>
    async Task ForwardAcpTranscriptAsync(AgentInstance agent, AcpTranscriptForwarder forwarder, CancellationToken ct) {
        try {
            await forwarder.RunAsync(ct);
        } catch (Exception ex) {
            LogAcpForwarderFaulted(ex, agent.Id);
        }
    }

    /// <summary>
    /// Time budget for the ACP bounded final-drain — how long
    /// <see cref="FinalDrainAcpTranscriptAsync"/> waits for the forwarder's run task to finish
    /// draining (after the runtime is disposed) before giving up and letting
    /// <see cref="FinalizeAgentRunAsync"/> proceed to <see cref="ServerConnection.EndAgentSessionAsync"/>
    /// regardless. Deliberately small and independent of <see cref="EndAgentSessionBudget"/> — a
    /// slow/stuck drain degrades to "no trailing transcript", never a stacked delay on top of the
    /// session-end budget, and never pins shutdown (the primary invariant this exists to protect).
    /// Settable so tests don't wait for the real value.
    /// </summary>
    internal TimeSpan AcpFinalDrainBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Disposes the ACP runtime FIRST so its <c>DisposeAsync</c> completes the
    /// transcript channel (courtesy-flushing any still-open aggregation run) — <paramref name="acpForwarder"/>'s
    /// run task can only ever return once that channel completes — then gives the forwarder a FINITE
    /// budget (<see cref="AcpFinalDrainBudget"/>) to drain whatever's left to the server before
    /// returning UNCONDITIONALLY. Never throws and never blocks past the budget: a disposal fault or
    /// a drain that exceeds it is logged, not propagated, so <see cref="FinalizeAgentRunAsync"/>'s
    /// own outage-cleanup guarantee (<see cref="EndAgentSessionBudget"/>) is never compounded by this
    /// call. This is also what keeps the forwarder stopped BEFORE the binding goes terminal (the
    /// caller ends the session immediately after this returns) — the ordering the hot-loop
    /// guard's edge case relies on in the normal (non-outage) flow.
    ///
    /// When the drain misses its budget, the forwarder is
    /// presumably still blocked/retrying a send against an unresponsive connection —
    /// <see cref="AcpTranscriptForwarder.RunAsync"/> otherwise retries indefinitely. Cancel
    /// <paramref name="agent"/>'s per-agent <see cref="AgentInstance.AcpCts"/> so it unwinds
    /// promptly (it is <c>ct</c>-aware at every await point) instead of leaking an orphaned task
    /// that keeps sending against an agent that's finalizing right now.
    /// </summary>
    async Task FinalDrainAcpTranscriptAsync(AgentInstance agent, AcpForwarderHandle acpForwarder) {
        try {
            await agent.Runtime.DisposeAsync();
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "disposing ACP runtime for final transcript drain", agent.Id);
        }

        var completed = await Task.WhenAny(acpForwarder.RunTask, Task.Delay(AcpFinalDrainBudget));

        if (completed != acpForwarder.RunTask) {
            LogAcpFinalDrainTimedOut(agent.Id, AcpFinalDrainBudget.TotalSeconds);

            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort — never let this pin teardown */ }
            }
        }
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
            // AI-1313 Phase B (D3): reap review-flow reviewers past their lifetime/idle backstop. Done
            // before the per-agent loop so a reaped reviewer isn't also heartbeated this tick. Reason
            // stamped on PendingEndReason so the end attribution is correct even if HandleStopAgent's
            // own end call loses to the read-loop's.
            foreach (var (id, reason) in FindReviewersToReap()) {
                if (_agents.TryGetValue(id, out var reviewer)) {
                    _logger.LogInformation(
                        "Reaping review-flow reviewer {AgentId} ({Reason}); age {AgeHours:F1}h, idle {IdleHours:F1}h",
                        id, reason, (ClockUtc() - reviewer.CreatedAt).TotalHours, (ClockUtc() - reviewer.LastOutputAt).TotalHours);
                    reviewer.PendingEndReason = reason;
                    _ = HandleStopAgent(id);
                }
            }

            // PrivateLocal agents get no heartbeats and no stuck-Starting auto-stop (deny-all;
            // the local user is present and drives them directly).
            foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
                // Detect agents stuck in "Starting" with no output
                if (agent.Status                         == "Starting" &&
                    DateTime.UtcNow - agent.LastOutputAt > StartupTimeout) {
                    LogAgentStuck(agent.Id, (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds, agent.Runtime.Pid, agent.Runtime.HasExited);
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

    async Task RunTokenRefreshLoopAsync(CancellationToken ct) {
        var loop = new TokenRefreshLoop(new TokenStoreRefreshPort(ProactiveRefreshWindow), _logger, ProactiveRefreshMinInterval);

        while (await _tokenRefresh.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Token refresh tick faulted — continuing loop");
            }
        }
    }

    async Task RunSpoolDrainLoopAsync(CancellationToken ct) {
        var loop = new SpoolDrainLoop(
            _config.ServerUrl,
            new HookSpool(PathHelpers.ConfigPath("spool")),
            new TranscriptSpool(PathHelpers.ConfigPath("transcript-spool")),
            _logger,
            onWhatsDoneRequested: SpawnWhatsDoneGenerator);

        while (await _spoolDrain.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Spool-drain tick faulted — continuing loop");
            }
        }
    }

    async Task CleanupAgentAsync(string agentId) {
        if (!_agents.TryRemove(agentId, out var agent)) {
            return;
        }

        // AI-1313 Phase B (D4 §6.4(2)): the agent has been removed from the registry as part of its
        // termination cleanup — its process is being/has been torn down, so drop the PID record.
        DeletePidRecord(agentId);

        // The reviewer process has exited by the time we get here (this runs off the read-loop's exit
        // path), so revoke its bridge token now — after any final submit_review_result was served.
        if (agent.ReviewerBridgeToken is { } reviewerToken) {
            try { _permissionBridge.RevokeReviewerToken(reviewerToken); } catch (Exception ex) { LogCleanupStepFailed(ex, "revoking reviewer bridge token", agentId); }
        }

        // Wake any attached local clients blocked on the user's stdin so they can flush the
        // last output and send Exited (the agent is going away). The exit code is already
        // captured on agent.Runtime, so disposing it below doesn't lose it.
        try { await agent.ExitedCts.CancelAsync(); } catch { /* best-effort */ }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Runtime.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

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
                await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
            } catch {
                /* best-effort */
            }
        }

        foreach (var agentId in _agents.Keys.ToList()) {
            await CleanupAgentAsync(agentId);
        }

        _heartbeatTimer.Dispose();
        _daemonHeartbeat.Dispose();
        _tokenRefresh.Dispose();
        _spoolDrain.Dispose();
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, vendor={Vendor})")]
    partial void LogAgentSpawned(string agentId, int pid, string worktree, string vendor);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to report resolved model for agent {AgentId} (continuing)")]
    partial void LogReportResolvedModelFailed(Exception ex, string agentId);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP bind/forwarder setup failed for agent {AgentId} — proceeding with no live transcript for this session")]
    partial void LogAcpBindFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP bind for agent {AgentId} resolved after the agent had already finalized — aborting setup without registering a binding")]
    partial void LogAcpBindAbortedAgentGone(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP transcript forwarder faulted for agent {AgentId}")]
    partial void LogAcpForwarderFaulted(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP final transcript drain for agent {AgentId} exceeded its {Seconds}s budget — proceeding to end the session; any undrained transcript is lost")]
    partial void LogAcpFinalDrainTimedOut(string agentId, double seconds);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();

    /// <summary>
    /// Method exposed for unit tests so they can drive <see cref="HandleLaunchAgent"/>
    /// without going through SignalR. Keeps the private handler private to everyone else.
    /// </summary>
    internal Task HandleLaunchAgentForTest(LaunchAgentCommand cmd) => HandleLaunchAgent(cmd);

    /// <summary>Test-only: register a pre-built agent so cleanup/lifecycle can be driven directly.</summary>
    internal void RegisterAgentForTest(AgentInstance agent) => _agents[agent.Id] = agent;

    /// <summary>Test-only: look up a tracked agent by id (null if absent), so a launch test can
    /// assert the resolved <see cref="AgentInstance.Work"/> / <see cref="AgentInstance.Worktree"/>.</summary>
    internal AgentInstance? GetAgentForTest(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Test-only entry point to the private cleanup path.</summary>
    internal Task CleanupAgentForTest(string agentId) => CleanupAgentAsync(agentId);

    /// <summary>Test-only: number of agents currently tracked (for awaiting cleanup).</summary>
    internal int ActiveAgentCountForTest => _agents.Count;

    /// <summary>Test-only entry point to the private stop handler (mirrors <see cref="HandleLaunchAgentForTest"/>).</summary>
    internal Task HandleStopAgentForTest(string agentId) => HandleStopAgent(agentId);

    /// <summary>Test-only entry point to the private send-input handler (AI-30 bracketed-paste submit).</summary>
    internal Task HandleSendInputForTest(SendInputCommand cmd) => HandleSendInput(cmd);

    /// <summary>Test-only entry point to the private probe-borrow-source handler (AI-1207 task A3).</summary>
    internal Task<BorrowProbeResult> HandleProbeBorrowSourceForTest(string path) => HandleProbeBorrowSource(path);

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
