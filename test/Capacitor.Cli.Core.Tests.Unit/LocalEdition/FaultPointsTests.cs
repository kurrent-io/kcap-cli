using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class FaultPointsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_unarmed_fault_point_is_a_no_op() {
        await Assert.That(() => FaultPoints.Hit("anything")).ThrowsNothing();
    }

    [Test]
    public async Task A_throwing_hook_aborts_the_write() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        using var lease = await LifecycleLease.AcquireAsync(root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        using var _ = FaultPoints.Arm(point => {
            if (point == $"renamed:{root.JournalPath}") throw new InvalidOperationException("crash");
        });

        await Assert.That(() => lease.Begin("setup", "start", ServerStart.Deny)).Throws<InvalidOperationException>();
        await Assert.That(File.Exists(root.JournalPath)).IsTrue();
    }

    [Test]
    public async Task A_state_write_hits_flush_rename_renamed_then_sync_in_order() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        using var lease = await LifecycleLease.AcquireAsync(root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        var hits = new List<string>();

        using var _ = FaultPoints.Arm(hits.Add);
        lease.Begin("setup", "start", ServerStart.Deny);

        await Assert.That(string.Join("|", hits)).IsEqualTo(
            $"flush:{root.JournalPath}|rename:{root.JournalPath}|renamed:{root.JournalPath}|sync:{root.Directory}");
    }
}
