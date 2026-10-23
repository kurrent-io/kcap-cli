using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

public class SessionEndFenceTests {
    static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    [TempDaemonPaths] public required TempDaemonStore Tmp { get; init; }

    ResumeLedgerStore Store => ResumeLedgerStore.For(Tmp.Store, "d1");

    ResumeLedgerStore Seed(ResumeState state, string? runEpoch = "epoch-1", string? launchEpoch = "epoch-1") {
        Store.Write(ResumeLedgerEntries.For("agent-1", state) with { RunEpoch = runEpoch, LaunchEpoch = launchEpoch, SessionId = "session-1" });
        return Store;
    }

    static bool Hold(string? agentId, string? epoch, ResumeLedgerStore? store, string? session = "session-1") =>
        SessionEndFence.ShouldHold(agentId, epoch, session, _ => store, new FakeTimeProvider(Now));

    [Test]
    public async Task No_entry_posts_and_leaves_no_lock_file() {
        await Assert.That(Hold("agent-1", "epoch-1", null)).IsFalse();
        await Assert.That(Hold(null, "epoch-1", Seed(ResumeState.Resuming))).IsFalse();
        await Assert.That(File.Exists(ResumeEntryLock.PathFor(Store.EntryPath("agent-1")))).IsFalse();
    }

    [Test]
    public async Task A_resuming_entry_holds() {
        await Assert.That(Hold("agent-1", "epoch-1", Seed(ResumeState.Resuming))).IsTrue();
        await Assert.That(Store.TryRead("agent-1")!.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task A_running_entry_under_the_same_epoch_posts_and_is_stamped() {
        var store = Seed(ResumeState.Running);

        await Assert.That(Hold("agent-1", "epoch-1", store)).IsFalse();

        var entry = store.TryRead("agent-1")!;
        await Assert.That(entry.EndCommittedAt).IsEqualTo(Now);
        await Assert.That(entry.EndCommittedEpoch).IsEqualTo("epoch-1");
        await Assert.That(entry.EndCommittedSession).IsEqualTo("session-1");
    }

    [Test]
    public async Task An_end_from_an_earlier_run_holds() {
        await Assert.That(Hold("agent-1", "epoch-1", Seed(ResumeState.Running, runEpoch: "epoch-2"))).IsTrue();
        await Assert.That(Store.TryRead("agent-1")!.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task An_end_without_an_epoch_holds_only_once_the_run_was_resumed() {
        await Assert.That(Hold("agent-1", null, Seed(ResumeState.Running))).IsFalse();
        await Assert.That(Hold("agent-1", null, Seed(ResumeState.Running, runEpoch: "epoch-2"))).IsTrue();
    }

    [Test]
    public async Task A_lock_held_elsewhere_lets_the_end_post() {
        var store = Seed(ResumeState.Resuming);
        using var held = ResumeEntryLock.TryAcquire(store.EntryPath("agent-1"), TimeSpan.FromSeconds(1));

        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => store, new FakeTimeProvider(Now), TimeSpan.FromMilliseconds(40)))
            .IsFalse();
    }

    [Test]
    public async Task An_unreadable_ledger_lets_the_end_post() {
        await Assert.That(SessionEndFence.ShouldHold("agent-1", "epoch-1", "session-1", _ => throw new IOException("broken"), new FakeTimeProvider(Now)))
            .IsFalse();
    }
}
