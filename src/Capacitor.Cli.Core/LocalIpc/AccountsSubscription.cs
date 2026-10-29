using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.LocalIpc;

public static class AccountsSubscription {
    /// The enumeration ending for any reason but caller cancellation means "this attempt is over" —
    /// the consumer decides whether to go again.
    public static async IAsyncEnumerable<AccountsStreamEvent> RunAsync(
            DaemonStore store, string daemonName, [EnumeratorCancellation] CancellationToken ct = default) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try {
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
                stream = new NetworkStream(sock, ownsSocket: false);
                await FrameCodec.WriteAsync(stream, LocalFrame.AccountsSubscribe(), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (ex is IOException or SocketException) {
                yield break;
            }

            yield return new AccountsStreamEvent.Subscribed();

            while (true) {
                LocalFrame? frame;
                try {
                    frame = await FrameCodec.ReadAsync(stream!, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) {
                    yield break;
                }
                if (frame is null) yield break;
                if (frame.Type != FrameType.AccountsSnapshot) yield break; // protocol confusion

                AccountsSnapshotDto? dto;
                try { dto = JsonSerializer.Deserialize(frame.Text, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto); }
                catch (JsonException) { yield break; }
                // Skipped, not fatal: ending here would make every resubscribe replay it.
                if (dto is null || string.IsNullOrWhiteSpace(dto.HostId) || dto.Revision < 0) continue;
                yield return new AccountsStreamEvent.Snapshot(dto);
            }
        } finally {
            if (stream is not null) await stream.DisposeAsync();
        }
    }
}
