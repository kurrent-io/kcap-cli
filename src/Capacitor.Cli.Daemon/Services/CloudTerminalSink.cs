using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The cloud mirror's end of an agent's PTY fan-out. <see cref="TryEnqueue"/> never blocks, so a
/// slow or absent server cannot back-pressure the PTY; the pump sends one chunk at a time, in
/// order, and only to a connection that has finished registering.
/// </summary>
internal sealed partial class CloudTerminalSink : ITerminalSink, IAsyncDisposable {
    readonly string                                        _agentId;
    readonly Lock                                          _sinksLock;
    readonly TerminalOutputBuffer                          _ring;
    readonly Func<string, string, CancellationToken, Task> _send;
    readonly Func<bool>                                    _isReady;
    readonly ILogger                                       _logger;
    readonly TimeProvider                                  _time;
    readonly CloudTerminalSinkOptions                      _options;
    readonly CancellationTokenSource                       _pumpCts;
    readonly CancellationTokenSource                       _deadlineCts;
    readonly Task                                          _pump;

    readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    static readonly byte[] Wake  = [];
    static readonly byte[] Reset = [0x1B, 0x63];

    volatile bool _completed;
    volatile bool _desynced;
    volatile bool _abortReplay;
    long          _queuedBytes;
    long          _deadline = long.MaxValue;
    Task?         _termination;
    int           _wakePending;
    long          _wakeupsWritten;

    // Guarded by the sinks lock.
    long _desyncs;
    long _unreportedDesyncs;
    long _lastWarning;
    bool _warned;

    // CancelAfter is banned (it can't take a TimeProvider); a shorter StopAsync call instead
    // supersedes this timer with a fresh one and disposes the one it replaces.
    CancellationTokenSource? _deadlineTimer;

    public CloudTerminalSink(
            string                                        agentId,
            Lock                                          sinksLock,
            TerminalOutputBuffer                          ring,
            Func<string, string, CancellationToken, Task> send,
            Func<bool>                                    isReady,
            ILogger                                       logger,
            TimeProvider                                  time,
            CloudTerminalSinkOptions                      options,
            CancellationToken                             shutdown
        ) {
        _agentId     = agentId;
        _sinksLock   = sinksLock;
        _ring        = ring;
        _send        = send;
        _isReady     = isReady;
        _logger      = logger;
        _time        = time;
        _options     = options;
        _pumpCts     = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _deadlineCts = new CancellationTokenSource();
        _pump        = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    public bool Detached => _completed;

    internal Task PumpForTest => _pump;

    // A single-reader unbounded channel cannot be counted, so the writes are.
    internal long WakeupsWrittenForTest => Interlocked.Read(ref _wakeupsWritten);

    /// <summary>The connection this sink was writing to is gone, so anything written to it may
    /// not have arrived, and a replay running on it is wasted.</summary>
    public void RequestResync() {
        lock (_sinksLock) {
            if (_completed) return;

            MarkDesyncedLocked("connection change", abortReplay: true);
        }
    }

    /// <summary>Caller holds the sinks lock.</summary>
    public void TryEnqueue(byte[] chunk) {
        if (_completed || _desynced) return;

        var queued = Interlocked.Read(ref _queuedBytes);

        // An empty queue always accepts, so an overflow implies a pump with work to wake on.
        if (queued > 0 && queued + chunk.Length > _options.BacklogBudgetBytes) {
            MarkDesyncedLocked("backlog over budget", abortReplay: false);

            return;
        }

        Interlocked.Add(ref _queuedBytes, chunk.Length);
        _queue.Writer.TryWrite(chunk);
    }

    /// <summary>Caller holds the sinks lock.</summary>
    void MarkDesyncedLocked(string cause, bool abortReplay) {
        var wasSynced = !_desynced;

        _desynced = true;
        if (abortReplay) _abortReplay = true;

        // One sentinel at most, however many requests arrive while the pump is held.
        if (Interlocked.Exchange(ref _wakePending, 1) == 0 && _queue.Writer.TryWrite(Wake)) {
            Interlocked.Increment(ref _wakeupsWritten);
        }

        if (wasSynced) NoteDesyncLocked(cause);
    }

    /// <summary>Caller holds the sinks lock.</summary>
    void NoteDesyncLocked(string cause) {
        _desyncs++;

        var now = _time.GetTimestamp();

        if (_warned && _time.GetElapsedTime(_lastWarning, now) < _options.WarningInterval) {
            _unreportedDesyncs++;

            return;
        }

        LogDesynced(_agentId, cause, _desyncs, _unreportedDesyncs);
        _unreportedDesyncs = 0;
        _lastWarning       = now;
        _warned            = true;
    }

    /// <summary>
    /// Idempotent and safe to call concurrently: every caller awaits the one termination, and the
    /// earliest deadline any of them asked for wins.
    /// </summary>
    public Task StopAsync(TimeSpan drainBound) {
        lock (_sinksLock) {
            if (!_completed) {
                _completed = true;
                _queue.Writer.TryComplete();
            }

            var immediate = drainBound <= TimeSpan.Zero;
            var deadline  = immediate
                ? long.MinValue
                : _time.GetTimestamp() + (long)(drainBound.TotalSeconds * _time.TimestampFrequency);

            if (deadline < _deadline) {
                _deadline = deadline;

                if (immediate) {
                    _deadlineTimer?.Dispose();
                    _deadlineTimer = null;
                    CancelDeadline();
                } else {
                    var timer = new CancellationTokenSource(drainBound, _time);
                    timer.Token.Register(CancelDeadline);
                    _deadlineTimer?.Dispose();
                    _deadlineTimer = timer;
                }
            }

            return _termination ??= TerminateAsync();
        }
    }

    void CancelDeadline() {
        try { _deadlineCts.Cancel(); }
        catch (ObjectDisposedException) {
            // Termination already finished.
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.Zero));

    async Task TerminateAsync() {
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (_deadlineCts.Token.Register(() => deadline.TrySetResult())) {
            await Task.WhenAny(_pump, deadline.Task);
        }

        if (!_pump.IsCompleted) {
            LogDrainCutShort(_agentId, Interlocked.Read(ref _queuedBytes));
            await _pumpCts.CancelAsync();
            await Task.WhenAny(_pump, Task.Delay(_options.CancellationGrace, _time));
        }

        if (_pump.IsCompleted) {
            DisposeSources();

            return;
        }

        // The send ignored its token. Finalization must not wait on it: let it finish on its own
        // and observe whatever it ends with.
        LogPumpAbandoned(_agentId);
        _ = _pump.ContinueWith(
            t => {
                _ = t.Exception;
                DisposeSources();
            },
            TaskScheduler.Default
        );
    }

    void DisposeSources() {
        _pumpCts.Dispose();
        _deadlineCts.Dispose();
        _deadlineTimer?.Dispose();
    }

    async Task PumpAsync(CancellationToken ct) {
        try {
            while (true) {
                ct.ThrowIfCancellationRequested();

                // Pending resync work comes before waiting on, or exiting from, the channel.
                if (_desynced) {
                    if (_completed || !await ResyncAsync(ct)) return;

                    continue;
                }

                if (_queue.Reader.TryRead(out var chunk)) {
                    if (ReferenceEquals(chunk, Wake)) {
                        Interlocked.Exchange(ref _wakePending, 0);

                        continue;
                    }

                    Interlocked.Add(ref _queuedBytes, -chunk.Length);
                    await SendAsync(chunk, replay: false, ct);

                    continue;
                }

                if (!await _queue.Reader.WaitToReadAsync(ct)) return;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Stop deadline or daemon shutdown.
        } catch (Exception ex) {
            LogPumpFaulted(ex, _agentId);
        }
    }

    /// <summary>False when the pump must exit: the sink completed before the replay began.</summary>
    async Task<bool> ResyncAsync(CancellationToken ct) {
        // Wait here rather than after the snapshot, so the snapshot is fresh when sending starts.
        while (!_isReady()) {
            if (_completed) return false;

            await Task.Delay(_options.RetryDelay, _time, ct);
        }

        List<byte[]> replay;

        lock (_sinksLock) {
            // Completion is set under this lock, so beginning a replay is atomic with it.
            if (_completed) return false;

            while (_queue.Reader.TryRead(out var stale)) {
                if (ReferenceEquals(stale, Wake)) Interlocked.Exchange(ref _wakePending, 0);
            }

            Interlocked.Exchange(ref _queuedBytes, 0);
            replay       = _ring.GetAll();
            _desynced    = false;
            _abortReplay = false;
        }

        if (!await SendAsync(Reset, replay: true, ct)) return true;

        foreach (var chunk in replay) {
            if (!await SendAsync(chunk, replay: true, ct)) return true;
        }

        LogResynced(_agentId, replay.Count);

        return true;
    }

    /// <summary>False when the chunk was given up because a desync superseded it. A budget
    /// overflow does not supersede a replay chunk; only <c>_abortReplay</c> does.</summary>
    async Task<bool> SendAsync(byte[] chunk, bool replay, CancellationToken ct) {
        var base64   = Convert.ToBase64String(chunk);
        var attempts = 0;

        while (true) {
            if (replay ? _abortReplay : _desynced) return false;

            if (!_isReady()) {
                attempts = 0;
                await Task.Delay(_options.RetryDelay, _time, ct);

                continue;
            }

            try {
                await _send(_agentId, base64, ct);

                return true;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                if (!_isReady()) {
                    attempts = 0;
                } else if (++attempts >= _options.FailingSendAttempts) {
                    lock (_sinksLock) MarkDesyncedLocked("send kept failing", abortReplay: true);

                    return false;
                }

                LogSendRetry(ex, _agentId);
                await Task.Delay(_options.RetryDelay, _time, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal output send for agent {AgentId} failed, holding the chunk and retrying")]
    partial void LogSendRetry(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal mirror drain for agent {AgentId} cut short with {QueuedBytes} bytes unsent")]
    partial void LogDrainCutShort(string agentId, long queuedBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal mirror pump for agent {AgentId} did not end after cancellation; abandoning it")]
    partial void LogPumpAbandoned(string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Terminal mirror pump for agent {AgentId} faulted")]
    partial void LogPumpFaulted(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal mirror for agent {AgentId} desynced ({Cause}); it will be reset and replayed. Desyncs so far: {Desyncs}, of which {Unreported} went unreported since the last warning")]
    partial void LogDesynced(string agentId, string cause, long desyncs, long unreported);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal mirror for agent {AgentId} resynced with a reset and {Chunks} replayed chunks")]
    partial void LogResynced(string agentId, int chunks);
}
