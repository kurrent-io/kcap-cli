using System.Diagnostics;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AccountReadingSinkTests {
    const string Work  = "c0dec001-work";
    const string Spare = "c0dec002-spare";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    AccountStore Store => field ??= new AccountStore(Tmp.PathTo("accounts"));

    void Register(string id, long generation = 0) {
        var entry = new VendorAccount(id, HarnessId.Codex, AccountDirectory.Normalize(Tmp.CreateDir(id)), id, DateTimeOffset.UnixEpoch, Generation: generation);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, entry] }, 0));
    }

    AccountReadingSink Sink() => new(Store, TimeProvider.System, NullLogger<AccountReadingSink>.Instance);

    static ResolvedAccount Account(string? id, long generation = 0) => new("/unused", id, id ?? "default", generation);

    static ReadingObservation Window(double used) =>
        new(At, Fresh: true, [new UsageWindowDto("codex", 300, used, At.AddHours(5), null)], null, null, [], false, true);

    ReadingFile? Read(string id) => Store.Readings.LoadAll().GetValueOrDefault(id);

    async Task<ReadingFile> WaitFor(string id, Func<ReadingFile, bool> condition, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true) {
            if (Read(id) is { } file && condition(file)) return file;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"no matching reading for {id}");
            await Task.Delay(10);
        }
    }

    /// <summary>The registered post behind the unregistered one proves the consumer reached it.</summary>
    [Test]
    public async Task Posts_for_an_unregistered_default_record_nothing() {
        Register(Work);
        using var sink = Sink();

        sink.Post(Account(null), Window(90));
        sink.Post(null, Window(91));
        sink.Post(Account(Work), Window(10));

        var file = await WaitFor(Work, _ => true);
        await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
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
        sink.Post(Account(Work), Window(100));
        await WaitFor(Work, f => f.Reading.Windows.Any(w => w.UsedPercent >= 100), TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Notification_from_a_retired_generation_is_dropped() {
        Register(Work, generation: 1);
        Register(Spare);
        using var sink = Sink();

        sink.Post(Account(Work, generation: 0), Window(80));
        sink.Post(Account(Spare), Window(5));

        await WaitFor(Spare, _ => true);
        await Assert.That(Read(Work)).IsNull();

        sink.Post(Account(Work, generation: 1), Window(20));
        var file = await WaitFor(Work, _ => true);
        await Assert.That(file.Reading.Generation).IsEqualTo(1L);
        await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(20.0);
    }
}
