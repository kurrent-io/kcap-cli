using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>A <c>claude -p</c> child: <see cref="Push"/> writes a line to its stdout, <see cref="NextWriteAsync"/>
/// reads what reached its stdin, and <see cref="EndOfStream"/> is it exiting.</summary>
internal sealed class FakeStreamJsonProcess : IJsonLineProcess {
    readonly Channel<string> _lines  = Channel.CreateUnbounded<string>();
    readonly Channel<string> _writes = Channel.CreateUnbounded<string>();

    public int     Pid         => 4343;
    public bool    HasExited   { get; private set; }
    public int?    ExitCode    { get; private set; }
    public string? Diagnostics => null;

    public void Push(string line) => _lines.Writer.TryWrite(line);

    public void EndOfStream() {
        HasExited = true;
        ExitCode  = 0;
        _lines.Writer.TryComplete();
    }

    /// <summary>The next line written to stdin.</summary>
    public Task<string> NextWriteAsync() => _writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);

    public bool HasPendingWrite => _writes.Reader.TryPeek(out _);

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (await _lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_lines.Reader.TryRead(out var line))
                yield return line;
    }

    public Task WriteLineAsync(string json, CancellationToken ct) {
        _writes.Writer.TryWrite(json);
        return Task.CompletedTask;
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public Task CloseInputAsync(TimeSpan timeout) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) {
        EndOfStream();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        _lines.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
