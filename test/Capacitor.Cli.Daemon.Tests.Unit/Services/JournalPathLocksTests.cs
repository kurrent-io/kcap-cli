using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class JournalPathLocksTests {
    [Test]
    public async Task Acquire_release_churn_leaves_the_map_empty() {
        var locks = new JournalPathLocks();
        for (var i = 0; i < 200; i++) {
            using var lease = await locks.AcquireAsync($"/j/{i}.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);
            await Assert.That(lease).IsNotNull();
        }
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task A_waiter_keeps_the_entry_alive_until_it_too_releases() {
        var locks = new JournalPathLocks();
        var owner = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);
        var waiter = locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
        owner!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
        (await waiter)!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task Timed_out_and_cancelled_waits_release_nothing_and_leave_no_entry() {
        var locks = new JournalPathLocks();
        var owner = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);

        var timedOut = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Assert.That(timedOut).IsNull();

        using var cts = new CancellationTokenSource(20);
        await Assert.That(async () => await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(5), cts.Token)).Throws<OperationCanceledException>();

        // Still held: a third waiter must not get in.
        await Assert.That(await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromMilliseconds(20), CancellationToken.None)).IsNull();
        owner!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task Two_spellings_of_one_path_share_one_entry() {
        var locks = new JournalPathLocks();
        using var tmp = new TempDir();
        var a = tmp.PathTo("j.jsonl");
        var b = Path.Combine(tmp.Path, ".", "j.jsonl");
        using var owner = await locks.AcquireAsync(a, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Assert.That(await locks.AcquireAsync(b, TimeSpan.FromMilliseconds(20), CancellationToken.None)).IsNull();
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
    }
}
