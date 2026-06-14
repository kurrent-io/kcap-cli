using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket entry points invoked by <see cref="LocalControlServer"/>.
internal partial class AgentOrchestrator {
    // Filled in D5 (local spawn) and E4 (agent list).
    public Task HandleLocalSpawnAsync(LocalFrame spawn, Stream stream, CancellationToken ct) => Task.CompletedTask;
    public Task HandleLocalListAsync(Stream stream, CancellationToken ct) => Task.CompletedTask;

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

            try {
                while (!ct.IsCancellationRequested) {
                    var f = await FrameCodec.ReadAsync(stream, ct);
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
