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

    volatile bool             _completed;
    long                      _queuedBytes;
    long                      _deadline = long.MaxValue;
    Task?                     _termination;
    // CancelAfter is banned (it can't take a TimeProvider); a shorter StopAsync call instead
    // supersedes this timer with a fresh one and disposes the one it replaces.
    CancellationTokenSource?  _deadlineTimer;

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

    /// <summary>Caller holds the sinks lock.</summary>
    public void TryEnqueue(byte[] chunk) {
        if (_completed) return;

        Interlocked.Add(ref _queuedBytes, chunk.Length);
        _queue.Writer.TryWrite(chunk);
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

                if (_queue.Reader.TryRead(out var chunk)) {
                    Interlocked.Add(ref _queuedBytes, -chunk.Length);
                    await SendAsync(chunk, ct);

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

    async Task SendAsync(byte[] chunk, CancellationToken ct) {
        var base64 = Convert.ToBase64String(chunk);

        while (true) {
            if (!_isReady()) {
                await Task.Delay(_options.RetryDelay, _time, ct);

                continue;
            }

            try {
                await _send(_agentId, base64, ct);

                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
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
}
