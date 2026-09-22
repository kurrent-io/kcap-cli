using System.Collections.Concurrent;
using System.Text;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Stands in for the hub send. <see cref="OnSend"/> runs before a chunk is recorded, so a
/// test can gate it, fail it, or watch its token.</summary>
sealed class CloudTerminalSinkMirror {
    static readonly byte[] ResetBytes = [0x1B, 0x63];

    int           _entered;
    volatile bool _ready = true;

    public ConcurrentQueue<byte[]>                Sent   { get; } = new();
    public Func<byte[], CancellationToken, Task>? OnSend { get; set; }
    public int                                    Entered => Volatile.Read(ref _entered);
    public bool                                   Ready   { get => _ready; set => _ready = value; }

    public async Task Send(string agentId, string base64, CancellationToken ct) {
        var bytes = Convert.FromBase64String(base64);
        Interlocked.Increment(ref _entered);
        if (OnSend is { } hook) await hook(bytes, ct);
        Sent.Enqueue(bytes);
    }

    public static bool IsReset(byte[] chunk) => chunk.AsSpan().SequenceEqual(ResetBytes);

    public string[] SentText() => [.. Sent.Select(c => IsReset(c) ? "<RIS>" : Encoding.UTF8.GetString(c))];

    /// <summary>What a terminal fed this stream would hold: everything after the last reset.</summary>
    public string[] Reconstruct() {
        var screen = new List<string>();

        foreach (var chunk in Sent) {
            if (IsReset(chunk)) screen.Clear();
            else screen.Add(Encoding.UTF8.GetString(chunk));
        }

        return [.. screen];
    }
}
