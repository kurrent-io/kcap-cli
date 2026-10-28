using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AccountsPublisherServerTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Throttle = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Timeout  = TimeSpan.FromSeconds(5);

    /// <summary>Ready but never started, so every send fails inside the hub.</summary>
    sealed class UnstartedServerConnection(ILogger<ServerConnection> logger) : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        new FixedCredentialSource(), NullLoggerFactory.Instance, logger, TimeProvider.System) {
        internal override bool IsReady => true;
    }

    /// <summary>Connected, with the hub invoke and send recorded, so readiness follows the real
    /// registration bracket.</summary>
    sealed class RegisteringServerConnection(DaemonConfig config) : ServerConnection(
        config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        readonly List<DaemonConnect>       _connects = [];
        readonly List<AccountsSnapshotDto> _sent     = [];

        public Func<Task>? DuringConnect { get; set; }

        public DaemonConnect[] Connects { get { lock (_connects) return [.. _connects]; } }
        public AccountsSnapshotDto[] Sent { get { lock (_sent) return [.. _sent]; } }

        internal override HubConnectionState HubState => HubConnectionState.Connected;

        internal override async Task InvokeDaemonConnectRawAsync(DaemonConnect connect) {
            lock (_connects) _connects.Add(connect);
            if (DuringConnect is { } during) await during();
        }

        internal override Task SendAccountsChangedRawAsync(AccountsSnapshotDto snapshot) {
            lock (_sent) _sent.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    AccountStore Store() => new(Home.PathTo("accounts"));

    void Add(AccountStore store, string label) {
        var account = new VendorAccount(Guid.NewGuid().ToString("N"), HarnessId.Claude,
            AccountDirectory.Normalize(Home.PathTo($".claude-{label}")), label, DateTimeOffset.UnixEpoch);
        store.Mutate(r => (r with { Accounts = [.. r.Accounts, account] }, 0));
    }

    AccountsPublisher Publisher(AccountStore store, TimeProvider time) =>
        new(new DaemonConfig { Accounts = store, DefaultDirectories = FrozenDictionary<HarnessId, string>.Empty },
            Home, time, NullLogger<AccountsPublisher>.Instance);

    static async Task WaitUntil(Func<bool> condition) {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task First_change_sends_at_once_and_a_burst_sends_the_last_change_after_thirty_seconds() {
        var store = Store();
        Add(store, "work");
        var time = new FakeTimeProvider();
        using var publisher = Publisher(store, time);
        var sent = new List<long>();
        publisher.SendToServer = s => {
            lock (sent) sent.Add(s.Revision);
            return Task.CompletedTask;
        };

        await publisher.RebuildAsync(CancellationToken.None);
        await Assert.That(Sent()).IsEquivalentTo([1L], CollectionOrdering.Matching);

        Add(store, "spare");
        await publisher.RebuildAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        Add(store, "third");
        await publisher.RebuildAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(20) - TimeSpan.FromTicks(1));
        await Assert.That(Sent()).IsEquivalentTo([1L], CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromTicks(1));
        await Assert.That(Sent()).IsEquivalentTo([1L, 3L], CollectionOrdering.Matching);

        time.Advance(Throttle);
        Add(store, "fourth");
        await publisher.RebuildAsync(CancellationToken.None);
        await Assert.That(Sent()).IsEquivalentTo([1L, 3L, 4L], CollectionOrdering.Matching);

        long[] Sent() {
            lock (sent) return [.. sent];
        }
    }

    /// <summary>A failed send ends as a debug line on the connection; the publisher keeps the
    /// connection as its sink and sends the next change through it.</summary>
    [Test]
    public async Task A_server_without_the_handler_logs_at_debug_and_keeps_the_connection() {
        var store = Store();
        Add(store, "work");
        var time = new FakeTimeProvider();
        var log  = new CapturingLogger<ServerConnection>();
        await using var connection = new UnstartedServerConnection(log);
        using var publisher = Publisher(store, time);
        publisher.SendToServer = connection.AccountsChangedAsync;

        await publisher.RebuildAsync(CancellationToken.None);
        await WaitUntil(() => Entries().Length == 1);
        time.Advance(Throttle);
        Add(store, "spare");
        await publisher.RebuildAsync(CancellationToken.None);
        await WaitUntil(() => Entries().Length == 2);

        await Assert.That(Entries().All(e => e is { Level: LogLevel.Debug, Message: var m } && m.Contains("AccountsChanged"))).IsTrue();
        await Assert.That(publisher.Current!.Revision).IsEqualTo(2L);

        (LogLevel Level, string Message)[] Entries() {
            lock (log.Entries) return [.. log.Entries];
        }
    }

    /// <summary>The publisher's own send of a change landing inside the registration bracket is dropped
    /// by the readiness guard; an unchanged snapshot is not sent again.</summary>
    [Test]
    public async Task A_change_between_the_connect_payload_and_registration_reaches_the_server_after_registration() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store, new FakeTimeProvider());
        await publisher.RebuildAsync(CancellationToken.None);
        await using var connection = new RegisteringServerConnection(
            new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root });
        connection.GetAccountsSnapshot = () => publisher.Current;
        publisher.SendToServer         = connection.AccountsChangedAsync;

        await connection.RegisterDaemonAsync(new RegistrationAttempt(UnattendedAdvertisement.None, _ => { }));
        await Assert.That(connection.Sent).IsEmpty();

        connection.DuringConnect = async () => {
            Add(store, "spare");
            await publisher.RebuildAsync(CancellationToken.None);
        };
        await connection.RegisterDaemonAsync(new RegistrationAttempt(UnattendedAdvertisement.None, _ => { }));

        await Assert.That(connection.Connects.Select(c => c.Accounts!.Revision)).IsEquivalentTo([1L, 1L], CollectionOrdering.Matching);
        await Assert.That(connection.Sent.Select(s => s.Revision)).IsEquivalentTo([2L], CollectionOrdering.Matching);
    }

    /// <summary>A reading change keeps the revision, so only a comparison by value sees it.</summary>
    [Test]
    public async Task A_reading_change_between_the_connect_payload_and_registration_reaches_the_server() {
        var store = Store();
        Add(store, "work");
        var id = store.Load().Accounts.Single().Id;
        using var publisher = Publisher(store, new FakeTimeProvider());
        await publisher.RebuildAsync(CancellationToken.None);
        await using var connection = new RegisteringServerConnection(
            new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root });
        connection.GetAccountsSnapshot = () => publisher.Current;
        publisher.SendToServer         = connection.AccountsChangedAsync;

        connection.DuringConnect = async () => {
            var recorded = store.Readings.TryRecord(id, 0, null, f => f with {
                Reading = f.Reading with { Windows = [new UsageWindowDto("five_hour", 300, 42, null, null)] },
            });
            if (!recorded) throw new InvalidOperationException("reading not recorded");
            await publisher.RebuildAsync(CancellationToken.None);
        };
        await connection.RegisterDaemonAsync(new RegistrationAttempt(UnattendedAdvertisement.None, _ => { }));

        await Assert.That(connection.Connects.Single().Accounts!.Accounts.Single().Reading).IsNull();
        var sent = connection.Sent.Single();
        await Assert.That(sent.Revision).IsEqualTo(1L);
        await Assert.That(sent.Accounts.Single().Reading!.Windows.Single().UsedPercent).IsEqualTo(42d);
    }
}
