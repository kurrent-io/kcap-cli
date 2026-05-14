using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using kapacitor.Auth;
using kapacitor.Commands;
using kapacitor.Config;
using kapacitor.Daemon.Pty;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

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
    public TerminalOutputBuffer OutputBuffer      { get; }      = new();

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
}

internal partial class AgentOrchestrator : IAsyncDisposable {
    readonly ConcurrentDictionary<string, AgentInstance> _agents = new();
    readonly DaemonConfig                                _config;
    readonly ServerConnection                            _server;
    readonly WorktreeManager                             _worktreeManager;
    readonly RepoMatcher                                 _repoMatcher;
    readonly IPtyProcessFactory                          _ptyFactory;
    readonly IHttpClientFactory                          _httpClientFactory;
    readonly LocalPermissionBridge                       _permissionBridge;
    readonly ILogger<AgentOrchestrator>                  _logger;
    readonly PeriodicTimer                               _heartbeatTimer  = new(TimeSpan.FromSeconds(30));
    // AI-79: heartbeat tightened from 60 s SendAsync to 15 s round-trip Ping.
    // Server's default ClientTimeoutInterval is 30 s, and the staging incident
    // showed daemons holding a displaced slot for nearly a minute before
    // anyone noticed; 15 s puts us comfortably under both.
    readonly PeriodicTimer                               _daemonHeartbeat = new(TimeSpan.FromSeconds(15));
    static readonly TimeSpan                             _pingDeadline    = TimeSpan.FromSeconds(10);
    readonly CancellationTokenSource                     _shutdownCts     = new();

    public AgentOrchestrator(
            DaemonConfig               config,
            ServerConnection           server,
            WorktreeManager            worktreeManager,
            RepoMatcher                repoMatcher,
            IPtyProcessFactory         ptyFactory,
            IHttpClientFactory         httpClientFactory,
            LocalPermissionBridge      permissionBridge,
            ILogger<AgentOrchestrator> logger
        ) {
        _config            = config;
        _server            = server;
        _worktreeManager   = worktreeManager;
        _repoMatcher       = repoMatcher;
        _ptyFactory        = ptyFactory;
        _httpClientFactory = httpClientFactory;
        _permissionBridge  = permissionBridge;
        _logger            = logger;

        // Wire up server commands
        _server.OnLaunchAgent             += HandleLaunchAgent;
        _server.OnStopAgent               += HandleStopAgent;
        _server.OnSendInput               += HandleSendInput;
        _server.OnSendSpecialKey          += HandleSendSpecialKey;
        _server.OnResizeTerminal          += HandleResizeTerminal;
        _server.OnReconnectedCallback     += ReRegisterAgents;
        _server.FindRepoForRemoteHandler  =  HandleFindRepoForRemote;

        _server.GetLiveAgentIds = () => _agents
            .Where(kvp => kvp.Value.Status is "Starting" or "Running")
            .Select(kvp => kvp.Key)
            .ToArray();

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
    }

    int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

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

            // TODO Task 14: dispatch through launcher.Prepare(ctx)
            // Originally: OverlayDirectory + SymlinkClaudeProjectDir + WriteMcpConfig +
            // TrustWorktreeInClaudeConfig + MergeToolPermissions. Moved to ClaudeLauncher.

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

            // TODO Task 14: dispatch through launcher.BuildArgs(ctx)
            // Originally: review-vs-default arg construction. Moved to ClaudeLauncher.BuildArgs.
            var args = new List<string>();

            var env = new Dictionary<string, string> {
                ["KAPACITOR_RENDERED_AGENT"] = "1",
                ["KAPACITOR_AGENT_ID"]       = agentId
            };

            if (!string.IsNullOrEmpty(_config.ServerUrl)) {
                env["KAPACITOR_URL"] = _config.ServerUrl;
            }

            // Tell the spawned Claude's permission-request hook where to find this
            // daemon's local SignalR bridge. Bypasses Cloudflare's HTTP timeout on
            // the server's /hooks/permission-request long-poll. CLI falls back to
            // KAPACITOR_URL if this var is absent (e.g. older CLI builds).
            if (_permissionBridge.BaseUrl is { } bridgeUrl) {
                env["KAPACITOR_DAEMON_URL"] = bridgeUrl;
            }

            if (isReview && cmd.Review is { } reviewEnv) {
                env["KAPACITOR_REVIEW_PR"] = reviewEnv.PrNumber.ToString();
            }

            var process = _ptyFactory.Spawn(_config.ClaudePath, args.ToArray(), worktree.Path, env);

            LogAgentSpawned(agentId, process.Pid, worktree.Path, _config.ClaudePath);

            var cts   = new CancellationTokenSource();
            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, cmd.Vendor, process, worktree, cts) {
                McpConfigPath = mcpConfigPath
            };
            _agents[agentId] = agent;

            // Notify server
            await _server.AgentRegisteredAsync(agentId, prompt, model, effort, repoPath);

            _ = _server.AppendAgentRunEventAsync(
                agentId,
                new AgentRunStarted(prompt, model, effort, repoPath, worktree.Path, vendor)
            );

            // Persist repo path and notify server so launch dialog updates
            _ = Task.Run(async () => {
                    try {
                        await RepoPathStore.AddAsync(repoPath);
                        await _server.UpdateRepoPathsAsync();
                    } catch (Exception ex) {
                        LogRepoPathPersistFailed(ex, agentId);
                    }
                }
            );

            // Start reading output
            _ = ReadAgentOutputAsync(agent);
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            // Clean up worktree if it was created but agent didn't start
            if (worktree != null) {
                // TODO Task 14: launcher.Cleanup(agent) — remove symlink via launcher
                // try { RemoveClaudeProjectSymlink(worktree.Path); } catch { /* best-effort */ }

                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }
            }

            // TODO Task 14: launcher.Cleanup(agent) — remove mcp config via launcher
            // if (mcpConfigPath is not null) {
            //     try { File.Delete(mcpConfigPath); } catch { /* best-effort */ }
            // }

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
                CreateNoWindow         = true
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"]     = "Never";

            using var proc = Process.Start(psi);

            if (proc is null) return null;

            using var cts = new CancellationTokenSource(GitGuardTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch { /* best-effort */ }

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
        try {
            await foreach (var data in agent.Process.ReadOutputAsync(agent.ReadCts.Token)) {
                agent.LastOutputAt      = DateTime.UtcNow;
                agent.HasReceivedOutput = true;

                if (agent.Status == "Starting") {
                    agent.Status = "Running";
                    _            = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
                }

                agent.OutputBuffer.Append(data);
                var base64 = Convert.ToBase64String(data);
                _ = _server.SendTerminalOutputAsync(agent.Id, base64);
            }
        } catch (OperationCanceledException) {
            /* expected on stop */
        } catch (Exception ex) {
            LogOutputReadError(ex, agent.Id);
        } finally {
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
                    // flagged as a launch failure (AI-572). HasReceivedOutput guards
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

                        _ = _server.LaunchFailedAsync(agent.Id, reason);
                    }

                    agent.Status = status;
                    _            = _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    _ = _server.AppendAgentRunEventAsync(
                        agent.Id,
                        new AgentRunStopped(stopReason, exitCode)
                    );
                }

                LogAgentExited(agent.Id, exitCode);

                // Tell the server to end the AgentSession. Claude doesn't reliably fire
                // its own session-end hook on SIGTERM/exit, so without this call the
                // session would stay "active" forever in the read model. Server-side is
                // idempotent — if claude did fire session-end first, this is a no-op.
                // Reason is read from agent.PendingEndReason so that if HandleStopAgent's
                // own call failed (transient SignalR error) and this finally-block call
                // is the one that lands, a user-initiated stop is still recorded as
                // "agent_stopped" rather than "agent_exited".
                try {
                    var result = await _server.EndAgentSessionAsync(agent.Id, agent.PendingEndReason);

                    // The daemon doesn't track sessionId on its own (only agentId), so
                    // the server returns it in the result. Spawn what's-done locally
                    // when the server says yes.
                    if (result.GenerateWhatsDone && result.SessionId is not null) {
                        SpawnWhatsDoneGenerator(result.SessionId);
                    }
                } catch (Exception ex) {
                    LogEndSessionFailed(ex, agent.Id);
                }

                // Clean up worktree and unregister from server
                await CleanupAgentAsync(agent.Id);
            } catch (Exception ex) {
                LogCleanupError(ex, agent.Id);
            }
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

            // Tell the server to end the session. Idempotent server-side: if claude
            // did fire session-end during the graceful window, this is a no-op
            // (returns GenerateWhatsDone=false and the CLI's session-end handler
            // already spawned the what's-done generator on its end).
            try {
                var result = await _server.EndAgentSessionAsync(agentId, agent.PendingEndReason);

                if (result.GenerateWhatsDone && result.SessionId is not null) {
                    SpawnWhatsDoneGenerator(result.SessionId);
                }
            } catch (Exception ex) {
                LogEndSessionFailed(ex, agentId);
            }

            // Cancel the read loop, then terminate the process if it didn't exit gracefully.
            // The read loop's finally block will handle CleanupAgentAsync.
            await agent.ReadCts.CancelAsync();
            await agent.Process.TerminateAsync(TimeSpan.FromSeconds(10));
        } catch (Exception ex) {
            LogStopError(ex, agentId);
        }
    }

    /// <summary>
    /// Spawns <c>kapacitor generate-whats-done {sessionId}</c> as a detached process.
    /// Used when the daemon-driven session-end path supplants claude's own session-end
    /// hook — claude normally spawns this generator from its CLI session-end handler,
    /// but when claude crashed or was killed before firing session-end the daemon has
    /// to do it instead. Best-effort: failure is logged but doesn't block other
    /// cleanup, and a missing kapacitor binary just means no what's-done summary.
    /// </summary>
    void SpawnWhatsDoneGenerator(string sessionId) {
        try {
            var psi = new ProcessStartInfo(_config.KapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KAPACITOR_URL"] = _config.ServerUrl
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

    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        if (_agents.TryGetValue(cmd.AgentId, out var agent)) {
            agent.Process.Resize((ushort)cmd.Cols, (ushort)cmd.Rows);
        }

        return Task.CompletedTask;
    }

    void ReRegisterAgents() {
        _ = ReRegisterAgentsAsync();

        return;

        async Task ReRegisterAgentsAsync() {
            foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
                try {
                    await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath);
                    await _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId);

                    // Replay terminal buffer so the UI has terminal history
                    foreach (var base64 in agent.OutputBuffer.GetAll().Select(Convert.ToBase64String)) {
                        await _server.SendTerminalOutputAsync(agent.Id, base64);
                    }
                } catch (Exception ex) {
                    LogReRegisterFailed(ex, agent.Id);
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
            foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
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
        var loop = new DaemonHeartbeatLoop(_server, _pingDeadline, _logger);

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

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Process.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

        // TODO Task 14: launcher.Cleanup(agent)
        // try { RemoveClaudeProjectSymlink(agent.Worktree.Path); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing symlink", agentId); }

        try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }

        // TODO Task 14: launcher.Cleanup(agent)
        // if (agent.McpConfigPath is not null) {
        //     try { File.Delete(agent.McpConfigPath); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing mcp config", agentId); }
        // }

        try { await _server.AgentUnregisteredAsync(agentId); } catch (Exception ex) { LogCleanupStepFailed(ex, "unregistering", agentId); }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating")]
    partial void LogAgentStuck(string agentId, double seconds, int pid, bool exited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error {Step} for agent {AgentId}")]
    partial void LogCleanupStepFailed(Exception ex, string step, string agentId);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Spawned what's-done generator for session {SessionId} (PID {Pid})")]
    partial void LogWhatsDoneSpawned(string sessionId, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to spawn what's-done generator for session {SessionId}")]
    partial void LogWhatsDoneSpawnFailed(Exception? ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();
}
