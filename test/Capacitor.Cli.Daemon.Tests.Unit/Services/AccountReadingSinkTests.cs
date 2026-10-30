using System.Collections.Concurrent;
using System.Diagnostics;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Each test awaits the consumer's own report on the observation it posted, and widens the producer
/// lock budget, so a loaded machine slows these tests down without skipping the write they wait for.</summary>
public class AccountReadingSinkTests {
    const string Work  = "c0dec001-work";
    const string Spare = "c0dec002-spare";

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly ConcurrentDictionary<ReadingObservation, TaskCompletionSource<bool>> _outcomes = new(ReferenceEqualityComparer.Instance);

    AccountStore Store => field ??= Widened(new AccountStore(Tmp.PathTo("accounts")));

    static AccountStore Widened(AccountStore store) {
        store.Readings.ProducerBudget = HangGuard;
        return store;
    }

    void Register(string id, long generation = 0, DateTimeOffset? startedAt = null) {
        var entry = new VendorAccount(id, HarnessId.Codex, AccountDirectory.Normalize(Tmp.CreateDir(id)), id, DateTimeOffset.UnixEpoch, Generation: generation, GenerationStartedAt: startedAt);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, entry] }, 0));
    }

    AccountReadingSink Sink() =>
        new(Store, TimeProvider.System, NullLogger<AccountReadingSink>.Instance) {
            Processed = (observation, recorded) => Outcome(observation).TrySetResult(recorded),
        };

    TaskCompletionSource<bool> Outcome(ReadingObservation observation) =>
        _outcomes.GetOrAdd(observation, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Whether the consumer wrote <paramref name="observation"/>, once it has dealt with it.</summary>
    Task<bool> Processed(ReadingObservation observation) => Outcome(observation).Task.WaitAsync(HangGuard);

    static ResolvedAccount Account(string? id) => new("/unused", id, id ?? "default");

    static ReadingObservation Window(double used) =>
        new(At, Fresh: true, [new UsageWindowDto("codex", 300, used, At.AddHours(5), null)], null, null, [], false, true);

    ReadingFile? Read(string id) => Store.Readings.LoadAll().GetValueOrDefault(id);

    /// <summary>The registered post behind the unregistered ones proves the consumer reached them.</summary>
    [Test]
    public async Task Posts_for_an_unregistered_default_record_nothing() {
        Register(Work);
        using var sink = Sink();
        var work = Window(10);

        sink.Post(Account(null), Window(90));
        sink.Post(null, Window(91));
        sink.Post(Account(Work), work);

        await Assert.That(await Processed(work)).IsTrue();
        await Assert.That(Read(Work)!.Reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
        await Assert.That(Store.Readings.LoadAll().Keys).IsEquivalentTo([Work]);
    }

    [Test]
    public async Task Post_never_blocks_when_the_lock_is_held() {
        Register(Work);
        using var sink = Sink();

        var lease = Store.Readings.TryLock(TimeSpan.FromSeconds(1));
        await Assert.That(lease).IsNotNull();
        var clock = Stopwatch.StartNew();
        try {
            for (var i = 0; i < 1000; i++) sink.Post(Account(Work), Window(i % 100));
            clock.Stop();
        } finally {
            lease!.Dispose();
        }

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        // The consumer works through a full queue of atomic replaces first.
        var last = Window(100);
        sink.Post(Account(Work), last);
        await Assert.That(await Processed(last)).IsTrue();
        await Assert.That(Read(Work)!.Reading.Windows.Any(w => w.UsedPercent >= 100)).IsTrue();
    }

    [Test]
    public async Task Observation_before_the_generation_start_is_skipped() {
        Register(Work, generation: 1, startedAt: At.AddMinutes(1));
        Register(Spare);
        using var sink = Sink();
        var early = Window(80);
        var spare = Window(5);

        sink.Post(Account(Work), early);
        sink.Post(Account(Spare), spare);

        await Assert.That(await Processed(early)).IsFalse();
        await Assert.That(await Processed(spare)).IsTrue();
        await Assert.That(Read(Work)).IsNull();
    }

    [Test]
    public async Task Observation_after_a_re_login_is_recorded_without_relaunch() {
        Register(Work);
        using var sink = Sink();
        var launched = Account(Work);

        Store.Mutate(r => (r with { Accounts = [r.Accounts.Single() with { Generation = 1, GenerationStartedAt = At.AddMinutes(-1) }] }, 0));
        var later = Window(20);
        sink.Post(launched, later);

        await Assert.That(await Processed(later)).IsTrue();
        var file = Read(Work)!;
        await Assert.That(file.Reading.Generation).IsEqualTo(1L);
        await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(20.0);
    }
}
