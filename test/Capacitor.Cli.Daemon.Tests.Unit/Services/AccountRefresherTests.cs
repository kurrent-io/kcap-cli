using System.Collections.Concurrent;
using System.Collections.Frozen;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AccountRefresherTests {
    const string Work  = "c0dec001-work";
    const string Spare = "c0dec002-spare";

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    static readonly TimeSpan Minute   = TimeSpan.FromMinutes(1);

    [TempHome] public required TempHome Home { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));

    AccountStore Store => field ??= Widened(new AccountStore(Home.PathTo("accounts")));

    static AccountStore Widened(AccountStore store) {
        store.Readings.ProducerBudget = HangGuard;
        return store;
    }

    void Register(string id) {
        var entry = new VendorAccount(id, HarnessId.Codex, AccountDirectory.Normalize(Home.CreateDir(id)), id, DateTimeOffset.UnixEpoch);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, entry] }, 0));
    }

    /// <summary>Counts probes per account; <see cref="Reached"/> makes every answer report a reached limit.</summary>
    sealed class CountingProbe(TimeProvider time) : IAccountProbe {
        readonly ConcurrentDictionary<string, int> _calls = new();

        public bool                  Reached { get; init; }
        public Func<string, bool>?   Throws  { get; init; }
        public TaskCompletionSource? Gate    { get; set; }
        public TaskCompletionSource  Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int this[string id] => _calls.GetValueOrDefault(id);
        public int Total           => _calls.Values.Sum();

        public async Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) {
            _calls.AddOrUpdate(account.Id, 1, (_, n) => n + 1);
            Entered.TrySetResult();
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Throws?.Invoke(account.Id) == true) throw new InvalidOperationException("probe exploded");

            var limits = Reached ? ReachedAt(time.GetUtcNow()) : null;
            return new ProbeResult(SignInStates.SignedIn, null, null, limits, null);
        }
    }

    static ReadingObservation ReachedAt(DateTimeOffset at) =>
        new(at, Fresh: true, [new UsageWindowDto("codex", 300, 100, at.AddHours(5), null)], null, null,
            [new LimitSignal("codex", null)], LimitReachedReported: true, LimitStateKnown: true);

    AccountRefresher Refresher(IAccountProbe probe, AccountStore? store = null) =>
        new(new DaemonConfig { Accounts = store ?? Store }, _ => probe, _time, NullLogger<AccountRefresher>.Instance);

    AccountsPublisher Publisher(Action<string> requested) =>
        new(new DaemonConfig { Accounts = Store, DefaultDirectories = FrozenDictionary<HarnessId, string>.Empty },
            Home, _time, NullLogger<AccountsPublisher>.Instance) { RequestRefresh = requested };

    /// <summary>A rollout or a menu scrape in another process, writing a block the way producers do.</summary>
    void ProducerBlocks(string id, DateTimeOffset at) {
        if (!Store.Readings.TryRecord(id, (f, _) => f with { Reading = ReadingMerge.Apply(f.Reading, ReachedAt(at)) }))
            throw new InvalidOperationException("the block was not recorded");
    }

    static Task Until(AccountRefresher refresher, Func<bool> condition) =>
        refresher.WaitForAsync(condition, CancellationToken.None).WaitAsync(HangGuard);

    /// <summary>Fake time fires a due deferral inside <c>Advance</c>, so once this returns nothing more is coming
    /// until time moves again.</summary>
    static Task Idle(AccountRefresher refresher) => Until(refresher, () => refresher.IsIdle);

    [Test]
    public async Task Refresher_probes_every_account_at_start() {
        Register(Work);
        Register(Spare);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);

        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 2);
            await Assert.That(probe[Work]).IsEqualTo(1);
            await Assert.That(probe[Spare]).IsEqualTo(1);
            await Assert.That(Store.Load().Accounts.All(a => a.RefreshedAt == _time.GetUtcNow())).IsTrue();
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Refresh_failure_never_stops_the_daemon() {
        Register(Work);
        Register(Spare);
        var probe = new CountingProbe(_time) { Throws = id => id == Work };
        using var refresher = Refresher(probe);

        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 2);
            await Assert.That(probe[Spare]).IsEqualTo(1);

            _time.Advance(Cooldown);
            refresher.Request(Work);
            await Until(refresher, () => refresher.Finished == 3);
            await Assert.That(probe[Work]).IsEqualTo(2);
            await Assert.That(refresher.ExecuteTask!.IsFaulted).IsFalse();
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task An_unreadable_registry_at_start_never_stops_the_daemon() {
        Home.CreateFile(["accounts", "accounts.json"], "{ not json");
        using var refresher = Refresher(new CountingProbe(_time));

        await refresher.StartAsync(CancellationToken.None);
        await Idle(refresher);

        await Assert.That(refresher.ExecuteTask!.IsFaulted).IsFalse();
        await refresher.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Duplicate_requests_coalesce() {
        Register(Work);
        var probe = new CountingProbe(_time) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var refresher = Refresher(probe);

        await refresher.StartAsync(CancellationToken.None);
        try {
            await probe.Entered.Task.WaitAsync(HangGuard);
            refresher.Request(Work);
            refresher.Request(Work);
            probe.Gate.TrySetResult();
            await Until(refresher, () => refresher.Finished == 1);

            refresher.Request(Work);
            refresher.Request(Work);
            _time.Advance(Cooldown);
            await Until(refresher, () => refresher.Finished == 2);

            for (var i = 0; i < 3; i++) _time.Advance(Cooldown);
            await Idle(refresher);
            await Assert.That(probe[Work]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Advanced_blockedAt_requests_one_refresh() {
        Register(Work);
        var requested = new ConcurrentQueue<string>();
        using var publisher = Publisher(requested.Enqueue);
        await publisher.StartAsync(CancellationToken.None);
        try {
            ProducerBlocks(Work, _time.GetUtcNow());
            await publisher.RebuildAsync(CancellationToken.None);
            await publisher.RebuildAsync(CancellationToken.None);
            await Assert.That(requested.ToArray()).IsEquivalentTo([Work]);

            ProducerBlocks(Work, _time.GetUtcNow().AddMinutes(1));
            await publisher.RebuildAsync(CancellationToken.None);
            await Assert.That(requested.ToArray()).IsEquivalentTo([Work, Work]);
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Refresh_reporting_a_reached_limit_does_not_trigger_itself() {
        Register(Work);
        var requested = new ConcurrentQueue<string>();
        using var publisher = Publisher(requested.Enqueue);
        await publisher.StartAsync(CancellationToken.None);
        try {
            await AccountRefresh.RunAsync(Store, Work, new CountingProbe(_time) { Reached = true }, _time, CancellationToken.None);
            await publisher.RebuildAsync(CancellationToken.None);

            await Assert.That(publisher.Current!.Accounts.Single().Reading!.Blocked!.BlockedAt).IsEqualTo(_time.GetUtcNow());
            await Assert.That(requested).IsEmpty();
        } finally {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Two_daemons_sharing_a_registry_settle_after_a_persistent_reached_limit() {
        Register(Work);
        var probe = new CountingProbe(_time) { Reached = true };
        using var refresherA = Refresher(probe);
        using var refresherB = Refresher(probe, new AccountStore(Store.Directory));
        using var publisherA = Publisher(refresherA.Request);
        using var publisherB = Publisher(refresherB.Request);
        await publisherA.StartAsync(CancellationToken.None);
        await publisherB.StartAsync(CancellationToken.None);
        await refresherA.StartAsync(CancellationToken.None);
        await refresherB.StartAsync(CancellationToken.None);
        try {
            await Until(refresherA, () => refresherA.Finished == 1);
            await Until(refresherB, () => refresherB.Finished == 1);

            for (var i = 0; i < 4; i++) {
                await publisherA.RebuildAsync(CancellationToken.None);
                await publisherB.RebuildAsync(CancellationToken.None);
                _time.Advance(Cooldown);
                await Idle(refresherA);
                await Idle(refresherB);
            }

            await Assert.That(publisherA.Current!.Accounts.Single().Reading!.Blocked!.BlockedAt).IsNotNull();
            await Assert.That(probe.Total).IsEqualTo(2);
        } finally {
            await refresherA.StopAsync(CancellationToken.None);
            await refresherB.StopAsync(CancellationToken.None);
            await publisherA.StopAsync(CancellationToken.None);
            await publisherB.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Genuine_hit_right_after_startup_refresh_runs_once_the_cooldown_ends() {
        Register(Work);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        using var publisher = Publisher(refresher.Request);
        await publisher.StartAsync(CancellationToken.None);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 1);

            _time.Advance(Minute);
            ProducerBlocks(Work, _time.GetUtcNow());
            await publisher.RebuildAsync(CancellationToken.None);

            _time.Advance(8 * Minute);
            await Idle(refresher);
            await Assert.That(probe[Work]).IsEqualTo(1);

            _time.Advance(Minute);
            await Until(refresher, () => refresher.Finished == 2);
            await Assert.That(probe[Work]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_soon_request_inside_the_cooldown_runs_at_once() {
        Register(Work);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 1);

            _time.Advance(Minute);
            refresher.RequestSoon(Work);
            await Until(refresher, () => refresher.Finished == 2);
            await Assert.That(probe[Work]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_second_soon_request_inside_the_cooldown_waits_for_it() {
        Register(Work);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 1);
            refresher.RequestSoon(Work);
            await Until(refresher, () => refresher.Finished == 2);

            _time.Advance(Minute);
            refresher.RequestSoon(Work);
            _time.Advance(8 * Minute);
            await Idle(refresher);
            await Assert.That(probe[Work]).IsEqualTo(2);

            _time.Advance(Minute);
            await Until(refresher, () => refresher.Finished == 3);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_deferred_soon_request_spends_the_budget_when_it_runs() {
        Register(Work);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 1);
            refresher.RequestSoon(Work);
            await Until(refresher, () => refresher.Finished == 2);

            refresher.RequestSoon(Work);
            _time.Advance(Cooldown);
            await Until(refresher, () => refresher.Finished == 3);

            refresher.RequestSoon(Work);
            _time.Advance(Cooldown - Minute);
            await Idle(refresher);
            await Assert.That(probe[Work]).IsEqualTo(3);

            _time.Advance(Minute);
            await Until(refresher, () => refresher.Finished == 4);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task The_soon_budget_runs_from_when_the_probe_starts() {
        Register(Work);
        Register(Spare);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 2);

            probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            refresher.RequestSoon(Work);
            refresher.RequestSoon(Spare);
            _time.Advance(3 * Minute);
            probe.Gate.SetResult();
            await Until(refresher, () => refresher.Finished == 4);

            _time.Advance(Cooldown - 2 * Minute);
            refresher.RequestSoon(Spare);
            await Idle(refresher);
            await Assert.That(probe[Spare]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task The_soon_budget_is_per_account() {
        Register(Work);
        Register(Spare);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 2);
            refresher.RequestSoon(Work);
            await Until(refresher, () => refresher.Finished == 3);

            _time.Advance(Minute);
            refresher.RequestSoon(Spare);
            await Until(refresher, () => refresher.Finished == 4);
            await Assert.That(probe[Spare]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_soon_request_replaces_a_pending_deferral() {
        Register(Work);
        var probe = new CountingProbe(_time);
        using var refresher = Refresher(probe);
        await refresher.StartAsync(CancellationToken.None);
        try {
            await Until(refresher, () => refresher.Finished == 1);
            refresher.Request(Work);

            _time.Advance(Minute);
            refresher.RequestSoon(Work);
            await Until(refresher, () => refresher.Finished == 2);

            _time.Advance(Cooldown);
            await Idle(refresher);
            await Assert.That(probe[Work]).IsEqualTo(2);
        } finally {
            await refresher.StopAsync(CancellationToken.None);
        }
    }
}
