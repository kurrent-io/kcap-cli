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

    static async Task WaitUntil(Func<bool> condition) {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

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
        await publisher.StartAsync(CancellationToken.None);
        publisher.Changed += s => changed.TrySetResult(s);
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

    /// The daemon reads a snapshot before its host starts the publisher; a registry change in between
    /// replaces one a reader already has, so start announces it.
    [Test]
    public async Task Start_announces_a_snapshot_that_replaces_one_built_before_it() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store, new FakeTimeProvider());
        await publisher.RebuildAsync(CancellationToken.None);
        var announced = new List<long>();
        publisher.Changed += s => { lock (announced) announced.Add(s.Revision); };
        Add(store, "spare");

        await publisher.StartAsync(CancellationToken.None);
        try {
            long[] revisions;
            lock (announced) revisions = [.. announced];
            await Assert.That(revisions).IsEquivalentTo([2L]);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    /// The rebuild the daemon runs before its host starts can fail; start then builds the first
    /// snapshot a subscriber sees, and announces it.
    [Test]
    public async Task Start_announces_the_first_snapshot_when_the_rebuild_before_it_failed() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store, new FakeTimeProvider());
        var loads = 0;
        publisher.LoadRegistry = s => Interlocked.Increment(ref loads) == 1 ? throw new IOException("sharing violation") : s.Load();
        await publisher.RebuildAsync(CancellationToken.None);
        await Assert.That(publisher.Current).IsNull();
        var announced = new List<long>();
        publisher.Changed += s => { lock (announced) announced.Add(s.Revision); };

        await publisher.StartAsync(CancellationToken.None);
        try {
            long[] revisions;
            lock (announced) revisions = [.. announced];
            await Assert.That(revisions).IsEquivalentTo([1L]);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    /// An unchanged revision is not a change, so the snapshot the daemon read before its host started
    /// is not announced again.
    [Test]
    public async Task Start_does_not_announce_the_snapshot_built_before_it_again() {
        var store = Store();
        Add(store, "work");
        using var publisher = Publisher(store, new FakeTimeProvider());
        await publisher.RebuildAsync(CancellationToken.None);
        var announced = 0;
        publisher.Changed += _ => Interlocked.Increment(ref announced);

        await publisher.StartAsync(CancellationToken.None);
        try {
            await Assert.That(Volatile.Read(ref announced)).IsEqualTo(0);
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
        await publisher.StartAsync(CancellationToken.None);
        publisher.Changed += s => {
            lock (changes) changes.Add(s);
            first.TrySetResult();
        };
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

    [Test]
    public async Task A_throwing_handler_neither_stops_the_poll_nor_fails_a_rebuild() {
        var store = Store();
        Add(store, "work");
        var time     = new FakeTimeProvider();
        var warnings = new Warnings();
        using var publisher = Publisher(store, time, logger: warnings);
        var seen = new List<long>();
        await publisher.StartAsync(CancellationToken.None);
        publisher.Changed += _ => throw new InvalidOperationException("subscriber bug");
        publisher.Changed += s => { lock (seen) seen.Add(s.Revision); };
        try {
            Add(store, "spare");
            time.Advance(Poll);
            await WaitUntil(() => { lock (seen) return seen.Count == 1; });

            Add(store, "third");
            time.Advance(Poll);
            await WaitUntil(() => { lock (seen) return seen.Count == 2; });

            Add(store, "fourth");
            await publisher.RebuildAsync(CancellationToken.None);

            long[] revisions;
            lock (seen) revisions = [.. seen];
            await Assert.That(revisions).IsEquivalentTo([2L, 3L, 4L]);
            await Assert.That(publisher.Current!.Revision).IsEqualTo(4L);
            await Assert.That(warnings.Captured.Warnings.Count).IsEqualTo(3);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The failure lands on the change tick and again on the settle read, the case that would
    /// otherwise mark the stat trusted and skip every later tick until the next write.</summary>
    [Test]
    public async Task A_transient_read_failure_is_retried_on_the_next_tick() {
        var store = Store();
        Add(store, "work");
        var time     = new FakeTimeProvider();
        var warnings = new Warnings();
        using var publisher = Publisher(store, time, logger: warnings);
        var changed = new TaskCompletionSource<AccountsSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await publisher.StartAsync(CancellationToken.None);
        publisher.Changed += s => changed.TrySetResult(s);
        var loads = 0;
        publisher.LoadRegistry = s =>
            Interlocked.Increment(ref loads) <= 2 ? throw new IOException("sharing violation") : s.Load();
        try {
            Add(store, "spare");
            time.Advance(Poll);
            await WaitUntil(() => Volatile.Read(ref loads) == 1);
            time.Advance(Poll);
            await WaitUntil(() => Volatile.Read(ref loads) == 2);
            time.Advance(Poll);

            var snapshot = await changed.Task.WaitAsync(Timeout);
            await Assert.That(snapshot.Revision).IsEqualTo(2L);
            await Assert.That(warnings.Captured.Warnings.Count).IsEqualTo(1);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }
}
