using System.Collections.Concurrent;
using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class RecordingTerminalSink : ITerminalSink {
    public ConcurrentQueue<string> Chunks { get; } = new();

    public bool Detached => false;

    public void TryEnqueue(byte[] chunk) => Chunks.Enqueue(Encoding.UTF8.GetString(chunk));
}
