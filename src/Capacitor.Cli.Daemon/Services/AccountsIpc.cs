using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Local-socket handler for AccountsSubscribe: the current snapshot at once, then each new
/// one, until the client goes away. Before the first snapshot exists it writes nothing and waits.</summary>
internal sealed class AccountsIpc(AccountsPublisher publisher) {
    int _subscribers;
    internal int ActiveSubscribersForTest => Volatile.Read(ref _subscribers);

    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        Interlocked.Increment(ref _subscribers);
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Without this a vanished subscriber would park the handler on the publisher forever.
            _ = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }, cts.Token);

            AccountsSnapshotDto? sent = null;
            while (true) {
                var seen = publisher.Version;
                if (publisher.Current is { } current && !ReferenceEquals(current, sent)) {
                    var json = JsonSerializer.Serialize(current, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);
                    await FrameCodec.WriteAsync(stream, FrameCodec.AccountsSnapshot(json), cts.Token);
                    sent = current;
                }

                await publisher.WaitBeyondAsync(seen, cts.Token);
            }
        } catch (OperationCanceledException) {
            // subscriber EOF or daemon shutdown
        } catch (IOException) {
            // the subscriber disconnected mid-write
        } catch (SocketException) {
            // the same, signalled by the transport
        } finally {
            Interlocked.Decrement(ref _subscribers);
        }
    }
}
