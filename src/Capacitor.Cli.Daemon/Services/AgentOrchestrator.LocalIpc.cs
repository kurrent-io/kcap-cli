using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket entry points invoked by <see cref="LocalControlServer"/>.
internal partial class AgentOrchestrator {
    /// <summary>Reply to a <c>kcap ls</c> request with a tab-separated agent table.</summary>
    public Task HandleLocalListAsync(Stream stream, CancellationToken ct) {
        var lines = _agents.Values.Select(a => $"{a.Id}\t{a.Status}\t{a.RepoPath}");

        return FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = string.Join('\n', lines) }, ct);
    }

    /// <summary>
    /// Spawn a new agent from a local <c>run-agent</c> request, then attach the requesting
    /// client. The agent runs <b>PrivateLocal</b> (no per-agent server calls) in either an
    /// owned worktree (<c>--worktree</c>) or the user's borrowed cwd (default in-place).
    /// </summary>
    public async Task HandleLocalSpawnAsync(LocalFrame spawn, Stream stream, CancellationToken ct) {
        var (vendor, work, cwd, args, cols, rows) = FrameCodec.Spawn(spawn);

        if (!_launchers.TryGetValue(vendor, out var launcher)) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Unknown vendor: {vendor}"), ct);
            return;
        }

        if (!Directory.Exists(cwd)) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Directory does not exist: {cwd}"), ct);
            return;
        }

        var           agentId = Guid.NewGuid().ToString("N");
        AgentInstance agent;

        try {
            var worktree = work == WorkLocation.OwnedWorktree
                ? await _worktreeManager.CreateAsync(cwd)
                : WorktreeInfo.Borrowed(cwd);

            var ctx = new LauncherContext(
                agentId, cwd, worktree, Prompt: null, Model: "", Effort: null,
                Tools: null, IsReview: false, Review: null, ReviewLaunch: null
            ) {
                Work = work
            };

            launcher.Prepare(ctx);
            var built = launcher.BuildPassthrough(ctx, args);

            // Phase 1 records as a plain local session. Keep KCAP_URL (records, under the
            // user's default_visibility) and re-add ANTHROPIC_API_KEY so normal local auth
            // survives UnixPtyProcess.Spawn's headless scrub (it applies extraEnv after
            // unsetenv). Omit the hosted-agent vars: KCAP_AGENT_ID (the agent isn't a
            // registered hosted agent yet, so no agent_host_id tag on events), and
            // KCAP_RENDERED_AGENT / KCAP_DAEMON_URL (not headless → permissions prompt
            // natively in the terminal). Phase 2 adds them when it registers like a UI agent.
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_config.ServerUrl)) env["KCAP_URL"] = _config.ServerUrl;
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(apiKey)) env["ANTHROPIC_API_KEY"] = apiKey;

            var proc = _ptyFactory.Spawn(launcher.CliPath, built.Args, worktree.Path, env, cols, rows);

            agent = new AgentInstance(agentId, null, "", null, cwd, vendor, proc, worktree, new CancellationTokenSource()) {
                IsPrivate     = true,
                Work          = work,
                McpConfigPath = built.McpConfigPath
            };
            _agents[agentId] = agent;
        } catch (Exception ex) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Launch failed: {ex.Message}"), ct);
            return;
        }

        _ = ReadAgentOutputAsync(agent);
        await AttachClientLoopAsync(agent, stream, ct);
    }

    /// <summary>Attach an existing agent to a local client (used by <c>kcap attach</c>).</summary>
    public Task HandleLocalAttachAsync(string agentId, Stream stream, CancellationToken ct) {
        if (!_agents.TryGetValue(agentId, out var agent))
            return FrameCodec.WriteAsync(stream, LocalFrame.Error($"no such agent {agentId}"), ct);

        return AttachClientLoopAsync(agent, stream, ct);
    }

    /// <summary>
    /// Registers a local sink, replays the agent's buffered output once, then pumps the
    /// client's input (stdin/resize) until it detaches or disconnects. The agent keeps
    /// running either way — the sink is just removed.
    /// </summary>
    internal async Task AttachClientLoopAsync(AgentInstance agent, Stream stream, CancellationToken ct) {
        // One NetworkStream, two writers (the sink's Stdout frames + Attached/Exited here):
        // serialise all writes through this lock. Reads (the input loop) are independent.
        var writeLock = new SemaphoreSlim(1, 1);

        async Task Send(LocalFrame f) {
            await writeLock.WaitAsync(ct);
            try { await FrameCodec.WriteAsync(stream, f, ct); } finally { writeLock.Release(); }
        }

        var sink = new LocalSocketSink(capacity: 4096, (chunk, _) => Send(LocalFrame.Stdout(chunk)));
        lock (agent.SinksLock) agent.LocalSinks.Add(sink);

        try {
            // Bounded replay BEFORE any live chunk so the client paints a coherent screen.
            await Send(FrameCodec.Attached(agent.Id, agent.OutputBuffer.Snapshot()));
            var pump = sink.RunAsync(ct);

            // Wake this read loop when the agent exits on its own (CleanupAgentAsync trips
            // ExitedCts), not only when the client sends input. Otherwise a self-exiting agent
            // (e.g. typing /exit) leaves us blocked on the client read and we never flush the
            // final output or send Exited — the client would just hang.
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct, agent.ExitedCts.Token);

            try {
                while (!loopCts.Token.IsCancellationRequested) {
                    var f = await FrameCodec.ReadAsync(stream, loopCts.Token);
                    if (f is null || f.Type == FrameType.Detach) break;

                    switch (f.Type) {
                        case FrameType.Stdin:  await agent.Process.WriteAsync(f.Bytes); break;
                        case FrameType.Resize: ApplyResizeClamp(agent, sink, f.Cols, f.Rows); break;
                    }
                }
            } catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) {
                /* client gone */
            } finally {
                sink.Complete();
                await pump.ConfigureAwait(false);
            }

            if (agent.Process.HasExited) {
                try { await Send(LocalFrame.Exited(agent.Process.ExitCode ?? 0)); } catch { /* client already gone */ }
            }
        } finally {
            lock (agent.SinksLock) {
                agent.LocalSinks.Remove(sink);
                agent.ClientDims.Remove(sink);
            }
        }
    }

    /// <summary>
    /// Min-clamp the one PTY across all attached local clients (tmux semantics): size it
    /// to the smallest cols × rows any client reports, so no client's redraw is corrupted.
    /// </summary>
    void ApplyResizeClamp(AgentInstance agent, ITerminalSink sink, ushort cols, ushort rows) {
        lock (agent.SinksLock) {
            agent.ClientDims[sink] = new AgentInstance.Dim(cols, rows);
            var c = agent.ClientDims.Values.Min(d => d.Cols);
            var r = agent.ClientDims.Values.Min(d => d.Rows);
            agent.Process.Resize(c == 0 ? cols : c, r == 0 ? rows : r);
        }
    }
}
