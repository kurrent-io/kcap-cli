using System.Collections.Frozen;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AccountsPublisherTests {
    [TempHome] public required TempHome Home { get; init; }

    static readonly TimeSpan Poll    = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    sealed class Warnings : ILogger<AccountsPublisher> {
        public CapturingLogger Captured { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Captured.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Captured.Log(logLevel, eventId, state, exception, formatter);
    }

    AccountStore Store() => new(Home.PathTo("accounts"));

    void Add(AccountStore store, string label) {
        var account = new VendorAccount(Guid.NewGuid().ToString("N"), HarnessId.Claude,
            AccountDirectory.Normalize(Home.PathTo($".claude-{label}")), label, DateTimeOffset.UnixEpoch);
        store.Mutate(r => (r with { Accounts = [.. r.Accounts, account] }, 0));
    }

    AccountsPublisher Publisher(AccountStore store, TimeProvider time,
            IReadOnlyDictionary<HarnessId, string>? defaults = null, ILogger<AccountsPublisher>? logger = null) =>
        new(new DaemonConfig { Accounts = store, DefaultDirectories = defaults ?? FrozenDictionary<HarnessId, string>.Empty },
            Home, time, logger ?? NullLogger<AccountsPublisher>.Instance);

    [Test]
    public async Task Start_builds_the_snapshot_with_host_id_revision_and_defaults() {
        var store = Store();
        Add(store, "work");
        Add(store, "spare");
        var claude = Home.PathTo(".claude");
        var codex  = Home.PathTo(".codex");
        using var publisher = Publisher(store, new FakeTimeProvider(),
            new Dictionary<HarnessId, string> { [HarnessId.Claude] = claude, [HarnessId.Codex] = codex });

        await publisher.StartAsync(CancellationToken.None);
        try {
            await Assert.That(publisher.Current).IsNotNull();
            var snapshot = publisher.Current!;
            await Assert.That(snapshot.HostId).IsEqualTo(store.HostId());
            await Assert.That(snapshot.Revision).IsEqualTo(2L);
            await Assert.That(snapshot.Accounts.Select(a => a.Label)).IsEquivalentTo(["work", "spare"]);
            await Assert.That(snapshot.Defaults.Select(d => $"{d.Vendor}={d.Directory}"))
                .IsEquivalentTo([$"claude={claude}", $"codex={codex}"]);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_registry_rewrite_republishes_within_the_debounce() {
        var store = Store();
        Add(store, "work");
        var time = new FakeTimeProvider();
        using var publisher = Publisher(store, time);
        var changed = new TaskCompletionSource<AccountsSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Changed += s => changed.TrySetResult(s);
        await publisher.StartAsync(CancellationToken.None);
        try {
            Add(store, "spare");
            time.Advance(Poll);

            var snapshot = await changed.Task.WaitAsync(Timeout);
            await Assert.That(snapshot.Revision).IsEqualTo(2L);
            await Assert.That(snapshot.Accounts.Select(a => a.Label)).IsEquivalentTo(["work", "spare"]);
            await Assert.That(publisher.Current).IsSameReferenceAs(snapshot);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_burst_of_rewrites_yields_one_snapshot() {
        var store = Store();
        var time  = new FakeTimeProvider();
        using var publisher = Publisher(store, time);
        var changes = new List<AccountsSnapshotDto>();
        var first   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Changed += s => {
            lock (changes) changes.Add(s);
            first.TrySetResult();
        };
        await publisher.StartAsync(CancellationToken.None);
        try {
            for (var i = 0; i < 10; i++) {
                Add(store, $"account-{i}");
                time.Advance(TimeSpan.FromMilliseconds(50));
            }
            time.Advance(Poll);
            await first.Task.WaitAsync(Timeout);
            time.Advance(Poll);
            await publisher.RebuildAsync(CancellationToken.None);

            AccountsSnapshotDto[] seen;
            lock (changes) seen = [.. changes];
            await Assert.That(seen.Select(s => s.Revision)).IsEquivalentTo([10L]);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_malformed_registry_keeps_the_last_snapshot() {
        var store = Store();
        Add(store, "work");
        var time     = new FakeTimeProvider();
        var warnings = new Warnings();
        using var publisher = Publisher(store, time, logger: warnings);
        await publisher.StartAsync(CancellationToken.None);
        try {
            var before  = publisher.Current;
            var changes = 0;
            publisher.Changed += _ => Interlocked.Increment(ref changes);

            await File.WriteAllTextAsync(store.RegistryPath, "{ not a registry");
            time.Advance(Poll);
            await publisher.RebuildAsync(CancellationToken.None);
            await publisher.RebuildAsync(CancellationToken.None);

            await Assert.That(publisher.Current).IsSameReferenceAs(before);
            await Assert.That(Volatile.Read(ref changes)).IsEqualTo(0);
            await Assert.That(warnings.Captured.Warnings.Count).IsEqualTo(1);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }
}
