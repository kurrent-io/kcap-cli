using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

/// A single local-client terminal output queue. The producer never blocks: a full queue
/// force-detaches the client, because losing a chunk mid-stream desyncs a redraw TUI's
/// cursor-addressing. A detached client reattaches for a fresh <c>OutputBuffer</c> replay.
internal sealed class LocalSocketSink : ITerminalSink {
    readonly Channel<byte[]>                       _ch;
    readonly Func<byte[], CancellationToken, Task> _send;

    public bool Detached { get; private set; }

    public LocalSocketSink(int capacity, Func<byte[], CancellationToken, Task> send) {
        _send = send;
        // Wait mode: TryWrite never blocks but returns false when the queue is full — that
        // false is our overflow signal (force-detach). DropOldest/DropWrite would silently
        // lose a chunk and always return true, reintroducing the corruption.
        _ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public void TryEnqueue(byte[] chunk) {
        if (Detached) return;

        if (!_ch.Writer.TryWrite(chunk)) {
            Detached = true;
            _ch.Writer.TryComplete();
        }
    }

    public void Complete() => _ch.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct) {
        try {
            await foreach (var chunk in _ch.Reader.ReadAllAsync(ct)) {
                try {
                    await _send(chunk, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch {
                    // Socket write failed — the client is gone. Drop it; don't wedge the loop.
                    Detached = true;
                    _ch.Writer.TryComplete();

                    return;
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            /* shutdown */
        }
    }
}
