using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptJournalSweepTests {
    static readonly DateTimeOffset Now = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    static string Journal(TempDir tmp, string agentId, TimeSpan age) {
        var path = Path.Combine(tmp.CreateDir("transcripts"), AgentFileNames.For(agentId) + ".jsonl");
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    static void PidRecord(TempDir tmp, string agentId) =>
        File.WriteAllText(Path.Combine(tmp.CreateDir("agents"), AgentFileNames.For(agentId) + ".json"), "{}");

    static TranscriptJournalSweep Sweep(TempDir tmp, FakeTimeProvider time, JournalPathLocks? locks = null) =>
        new(tmp.Path, time, NullLogger<TranscriptJournalSweep>.Instance, locks);

    [Test]
    public async Task Deletes_only_old_journals_without_a_pid_record() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var old        = Journal(tmp, "old", TimeSpan.FromDays(31));
        var fresh      = Journal(tmp, "fresh", TimeSpan.FromDays(1));
        var oldButLive = Journal(tmp, "live", TimeSpan.FromDays(31)); PidRecord(tmp, "live");
        var other      = Path.Combine(tmp.Path, "transcripts", "notes.txt"); File.WriteAllText(other, "x"); File.SetLastWriteTimeUtc(other, (Now - TimeSpan.FromDays(40)).UtcDateTime);

        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None);

        await Assert.That(File.Exists(old)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
        await Assert.That(File.Exists(oldButLive)).IsTrue();
        await Assert.That(File.Exists(other)).IsTrue();
    }

    [Test]
    public async Task Skips_a_journal_whose_lock_is_held() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var locks = new JournalPathLocks();
        var old = Journal(tmp, "old", TimeSpan.FromDays(31));
        using var held = await locks.AcquireAsync(old, TimeSpan.FromSeconds(1), CancellationToken.None);

        await Sweep(tmp, time, locks).RunOnceAsync(CancellationToken.None);

        await Assert.That(File.Exists(old)).IsTrue();
    }

    [Test]
    public async Task Missing_directory_and_enumeration_faults_never_escape() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None); // no transcripts dir
        tmp.CreateFile("transcripts"); // a file where the directory belongs: enumeration throws
        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None);
    }

    [Test]
    public async Task Runs_again_after_24_hours_without_a_restart() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var sweep = Sweep(tmp, time);
        await sweep.StartAsync(CancellationToken.None);
        await Assert.That(sweep.SweepsStarted).IsEqualTo(0);

        var journal = Journal(tmp, "old", TimeSpan.FromDays(31));
        time.Advance(TimeSpan.FromHours(24));
        await WaitUntil(() => sweep.SweepsStarted == 1);
        await Assert.That(File.Exists(journal)).IsFalse();

        time.Advance(TimeSpan.FromHours(24));
        await WaitUntil(() => sweep.SweepsStarted == 2);
        await sweep.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task A_tick_during_a_sweep_is_skipped() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var locks = new JournalPathLocks();
        var old = Journal(tmp, "old", TimeSpan.FromDays(31));
        using var held = await locks.AcquireAsync(old, TimeSpan.FromSeconds(1), CancellationToken.None); // the sweep waits ≤ LockBound on this
        var sweep = Sweep(tmp, time, locks);

        var first = sweep.RunOnceAsync(CancellationToken.None);
        var second = sweep.RunOnceAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        await Assert.That(sweep.SweepsStarted).IsEqualTo(1);
    }

    static async Task WaitUntil(Func<bool> condition) {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }
}
