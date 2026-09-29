using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>Emits what the test tells it to, when it tells it to, and ends on <see cref="Exit"/>.</summary>
sealed class ScriptedPtyProcess : IPtyProcess {
    readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();

    public int  Pid       => 4343;
    public bool HasExited => _output.Reader.Completion.IsCompleted;
    public int? ExitCode  => 0;

    public void Emit(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void Exit()            => _output.Writer.TryComplete();

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => _output.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? _) {
        Exit();

        return Task.CompletedTask;
    }

    public Task WriteAsync(string _) => Task.CompletedTask;
    public Task WriteAsync(byte[] _) => Task.CompletedTask;
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
