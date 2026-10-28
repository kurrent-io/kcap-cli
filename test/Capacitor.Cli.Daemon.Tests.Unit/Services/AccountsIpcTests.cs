using System.IO.Pipelines;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AccountsIpcTests {
    [TempHome] public required TempHome Home { get; init; }

    AccountStore Store() => new(Home.PathTo("accounts"));

    void Add(AccountStore store, string label) {
        var account = new VendorAccount(Guid.NewGuid().ToString("N"), HarnessId.Codex,
            AccountDirectory.Normalize(Home.PathTo($".codex-{label}")), label, DateTimeOffset.UnixEpoch);
        store.Mutate(r => (r with { Accounts = [.. r.Accounts, account] }, 0));
    }

    AccountsPublisher Publisher(AccountStore store) =>
        new(new DaemonConfig { Accounts = store }, Home, new FakeTimeProvider(), NullLogger<AccountsPublisher>.Instance);

    static async Task<AccountsSnapshotDto> NextSnapshot(Stream client, CancellationToken ct) {
        var frame = await FrameCodec.ReadAsync(client, ct);
        await Assert.That(frame?.Type).IsEqualTo(FrameType.AccountsSnapshot);
        return JsonSerializer.Deserialize(frame!.Text, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto)!;
    }

    [Test]
    public async Task Subscribe_writes_the_current_snapshot_then_each_change() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store);
        await publisher.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var toDaemon = new Pipe();
        var toClient = new Pipe();
        var client   = toClient.Reader.AsStream();

        var ipc     = new AccountsIpc(publisher);
        var handler = ipc.HandleSubscribeAsync(
            new DuplexTestStream(toDaemon.Reader.AsStream(), toClient.Writer.AsStream()), cts.Token);
        try {
            var initial = await NextSnapshot(client, cts.Token);
            await Assert.That(initial.HostId).IsEqualTo(store.HostId());
            await Assert.That(initial.Revision).IsEqualTo(1L);
            await Assert.That(ipc.ActiveSubscribersForTest).IsEqualTo(1);

            Add(store, "spare");
            await publisher.RebuildAsync(cts.Token);
            var second = await NextSnapshot(client, cts.Token);
            await Assert.That(second.Revision).IsEqualTo(2L);
            await Assert.That(second.Accounts.Select(a => a.Label)).IsEquivalentTo(["work", "spare"]);

            Add(store, "third");
            await publisher.RebuildAsync(cts.Token);
            await Assert.That((await NextSnapshot(client, cts.Token)).Revision).IsEqualTo(3L);
        } finally {
            await toDaemon.Writer.CompleteAsync();
        }

        await handler.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ipc.ActiveSubscribersForTest).IsEqualTo(0);
        await publisher.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Subscribe_before_the_first_snapshot_waits_for_it() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var toDaemon = new Pipe();
        var toClient = new Pipe();
        var client   = toClient.Reader.AsStream();

        var handler = new AccountsIpc(publisher).HandleSubscribeAsync(
            new DuplexTestStream(toDaemon.Reader.AsStream(), toClient.Writer.AsStream()), cts.Token);
        try {
            await publisher.StartAsync(cts.Token);

            await Assert.That((await NextSnapshot(client, cts.Token)).Revision).IsEqualTo(1L);
        } finally {
            await toDaemon.Writer.CompleteAsync();
            await publisher.StopAsync(CancellationToken.None);
        }

        await handler.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
