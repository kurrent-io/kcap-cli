using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class AccountReadingsTests {
    static DaemonAccountsDto Row(string daemon, string hostId, params string[] labels) => new() {
        DaemonName = daemon, HostId = hostId, Revision = 1,
        Accounts = [.. labels.Select(l => new ListedAccountDto {
            Id = l, Vendor = "claude", Directory = "/u/" + l, DirectoryKey = "/u/" + l, Label = l, RecordingState = "recording",
        })],
    };

    static (AccountReadings Readings, BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> Remote) Build() {
        var remote = new BehaviorSubject<IReadOnlyList<DaemonAccountsDto>>([]);
        return (new AccountReadings(Observable.Return<AccountsSnapshotDto?>(null), remote, Observable.Never<DateTimeOffset>()), remote);
    }

    [Test]
    public async Task Remote_picks_the_named_daemons_drawable_snapshot() {
        var (readings, remote) = Build();
        var seen = new List<AccountsSnapshotDto?>();
        using var sub = readings.Remote("daemon-b").Subscribe(seen.Add);

        remote.OnNext([Row("daemon-a", "h1", "x"), Row("daemon-b", "h2", "y", "z")]);

        await Assert.That(seen.Last()!.HostId).IsEqualTo("h2");
        await Assert.That(seen.Last()!.Accounts.Select(a => a.Id)).IsEquivalentTo(new[] { "y", "z" });
    }

    [Test]
    public async Task Remote_is_null_once_the_gate_closes() {
        var (readings, remote) = Build();
        var seen = new List<AccountsSnapshotDto?>();
        using var sub = readings.Remote("daemon-b").Subscribe(seen.Add);

        remote.OnNext([Row("daemon-b", "h2", "y")]);
        remote.OnNext([]);

        await Assert.That(seen.Last()).IsNull();
    }

    [Test]
    public async Task Remote_repeats_nothing_for_an_equivalent_snapshot() {
        var (readings, remote) = Build();
        var count = 0;
        using var sub = readings.Remote("daemon-b").Subscribe(_ => count++);

        remote.OnNext([Row("daemon-b", "h2", "y")]);
        remote.OnNext([Row("daemon-b", "h2", "y"), Row("daemon-a", "h1", "x")]);

        await Assert.That(count).IsEqualTo(2);
    }

    sealed class Stream {
        readonly Channel<AccountsStreamEvent> _channel = Channel.CreateUnbounded<AccountsStreamEvent>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<AccountsStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) yield return evt;
        }

        public void Emit(AccountsStreamEvent evt) => _channel.Writer.TryWrite(evt);
    }

    [Test]
    public async Task Local_follows_the_accounts_service_including_null_on_detach() {
        var daemon = new FakeDaemonClientService();
        var stream = new Stream();
        using var service = new AccountsService(daemon, stream.RunAsync, TimeProvider.System, CancellationToken.None);
        var readings = new AccountReadings(service.Snapshot, Observable.Return<IReadOnlyList<DaemonAccountsDto>>([]), Observable.Never<DateTimeOffset>());
        AccountsSnapshotDto? held = null;
        using var sub = readings.Local.Subscribe(s => Volatile.Write(ref held, s));

        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["status/1", "accounts/1"]));
        await WaitUntilAsync(() => stream.Attempts == 1, what: "the subscribe attempt");
        stream.Emit(new AccountsStreamEvent.Subscribed());
        stream.Emit(new AccountsStreamEvent.Snapshot(new("host-1", 1, [], [])));
        await WaitUntilAsync(() => Volatile.Read(ref held) is not null, what: "the snapshot");

        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await WaitUntilAsync(() => Volatile.Read(ref held) is null, what: "null after detach");
    }
}
