using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>Records every write, emits the output a test hands it, and runs until <see cref="Exit"/>.</summary>
sealed class RecordingScriptedPtyProcess : IPtyProcess {
    readonly Channel<byte[]>      _output = Channel.CreateUnbounded<byte[]>();
    readonly List<string>         _writes = [];
    readonly Lock                 _gate   = new();
    readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Writes {
        get { lock (_gate) { return [.. _writes]; } }
    }

    public int  Pid       => 6161;
    public bool HasExited => _exited.Task.IsCompleted;
    public int? ExitCode  => HasExited ? 0 : null;

    public bool IsInputLineEdited { get; set; }

    public void Emit(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Exit() {
        _output.Writer.TryComplete();
        _exited.TrySetResult();
    }

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => _output.Reader.ReadAllAsync(ct);

    public Task WriteAsync(string input) {
        lock (_gate) { _writes.Add(input); }

        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data) {
        lock (_gate) { _writes.Add(Encoding.UTF8.GetString(data)); }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Exit(); return default; }
    public Task WaitForExitAsync(TimeSpan? _) => _exited.Task;
    public Task TerminateAsync(TimeSpan?   _) { Exit(); return Task.CompletedTask; }
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
