using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class AccountsSubscriptionTests {
    delegate Task ConnScript(Socket raw, NetworkStream s, CancellationToken ct);

    sealed class ScriptedServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly Task _accept;

        public ScriptedServer(string sockPath, ConnScript script) {
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(c, s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            try { await _accept; } catch { }
        }
    }

    /// Reads the AccountsSubscribe frame, then writes each given payload as a frame of the given type.
    static ConnScript SubscribeThen(params (FrameType Type, string Text)[] frames) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.AccountsSubscribe) return;
        foreach (var (type, text) in frames) await FrameCodec.WriteAsync(s, new LocalFrame(type) { Text = text }, ct);
    };

    static ConnScript SubscribeThenPark() => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.AccountsSubscribe) return;
        await new TaskCompletionSource().Task.WaitAsync(ct);
    };

    static (FrameType, string) SnapshotFrame(string hostId, long revision, params AccountViewDto[] accounts) =>
        (FrameType.AccountsSnapshot, JsonSerializer.Serialize(
            new AccountsSnapshotDto(hostId, revision, accounts, []), AccountsSnapshotJsonContext.Default.AccountsSnapshotDto));

    static async Task WithServerAsync(ConnScript script, Func<DaemonStore, string, Task> body) {
        using var daemons = new TempDaemonStore();
        const string name = "accounts";
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), script);
        await body(daemons.Store, name);
    }

    static async Task<List<AccountsStreamEvent>> CollectAsync(DaemonStore store, string daemonName) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AccountsStreamEvent>();
        await foreach (var e in AccountsSubscription.RunAsync(store, daemonName, cts.Token)) events.Add(e);
        return events;
    }

    [Test]
    public async Task A_subscribe_yields_subscribed_then_each_snapshot_frame() {
        if (OperatingSystem.IsWindows()) return;

        var account = new AccountViewDto("a1", "claude", "/home/me/.claude", "k1", "Work", "recording");
        await WithServerAsync(SubscribeThen(SnapshotFrame("h1", 1, account), SnapshotFrame("h1", 2)), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(3);
            await Assert.That(events[0]).IsTypeOf<AccountsStreamEvent.Subscribed>();
            var first = ((AccountsStreamEvent.Snapshot)events[1]).Value;
            await Assert.That(first.HostId).IsEqualTo("h1");
            await Assert.That(first.Revision).IsEqualTo(1L);
            await Assert.That(first.Accounts.Single()).IsEqualTo(account);
            await Assert.That(((AccountsStreamEvent.Snapshot)events[2]).Value.Revision).IsEqualTo(2L);
        });
    }

    [Test]
    public async Task A_snapshot_without_identity_is_skipped_not_fatal() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync(SubscribeThen(SnapshotFrame("", 1), SnapshotFrame("h1", 2)), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(2);
            await Assert.That(((AccountsStreamEvent.Snapshot)events[1]).Value.HostId).IsEqualTo("h1");
        });
    }

    [Test]
    public async Task A_negative_revision_or_a_null_body_is_skipped_not_fatal() {
        if (OperatingSystem.IsWindows()) return;

        var frames = new[] { SnapshotFrame("h1", -1), (FrameType.AccountsSnapshot, "null"), SnapshotFrame("h1", 3) };
        await WithServerAsync(SubscribeThen(frames), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(2);
            await Assert.That(((AccountsStreamEvent.Snapshot)events[1]).Value.Revision).IsEqualTo(3L);
        });
    }

    [Test]
    public async Task Undecodable_json_ends_the_attempt() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync(SubscribeThen((FrameType.AccountsSnapshot, "not-json"), SnapshotFrame("h1", 1)), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<AccountsStreamEvent.Subscribed>();
        });
    }

    [Test]
    public async Task A_foreign_frame_ends_the_attempt() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync(SubscribeThen((FrameType.DaemonStatus, "{}"), SnapshotFrame("h1", 1)), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<AccountsStreamEvent.Subscribed>();
        });
    }

    [Test]
    public async Task A_closed_socket_ends_the_attempt_without_throwing() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync(SubscribeThen(), async (store, name) => {
            var events = await CollectAsync(store, name);

            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<AccountsStreamEvent.Subscribed>();
        });
    }

    [Test]
    public async Task A_failed_connect_yields_nothing() {
        if (OperatingSystem.IsWindows()) return;

        using var daemons = new TempDaemonStore();

        var events = await CollectAsync(daemons.Store, "nobody");

        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Caller_cancellation_throws_OperationCanceledException() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync(SubscribeThenPark(), async (store, name) => {
            using var cts = new CancellationTokenSource();
            await using var enumerator = AccountsSubscription.RunAsync(store, name, cts.Token).GetAsyncEnumerator();

            await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(enumerator.Current).IsTypeOf<AccountsStreamEvent.Subscribed>();

            var pending = enumerator.MoveNextAsync().AsTask();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        });
    }
}
