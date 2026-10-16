using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LifecycleLeaseTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    [Test]
    public async Task A_held_lock_turns_away_a_second_taker_once_its_wait_runs_out() {
        using var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        await Assert.That(() => Acquire(TimeSpan.FromMilliseconds(300), CancellationToken.None))
            .Throws<LocalLifecycleException>();
    }

    [Test]
    public async Task A_waiting_taker_gets_the_lock_when_the_holder_releases_it() {
        var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        var waiting = LifecycleLease.AcquireAsync(Root, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Task.Delay(200);
        await Assert.That(waiting.IsCompleted).IsFalse();

        held.Dispose();

        using var next = await waiting;
        await Assert.That(next.Root.Directory).IsEqualTo(Root.Directory);
    }

    [Test]
    public async Task A_cancelled_wait_stops_waiting() {
        using var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        using var cts  = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.That(() => Acquire(TimeSpan.FromMinutes(5), cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task The_lock_file_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        using var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        await Assert.That(File.GetUnixFileMode(Root.LockPath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Begin_refuses_to_overwrite_an_unrecovered_operation() {
        using var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        lease.Begin("setup", "staged", serverStartAllowed: false);

        await Assert.That(() => lease.Begin("upgrade", "staged", serverStartAllowed: false)).Throws<InvalidOperationException>();
    }

    async Task Acquire(TimeSpan wait, CancellationToken ct) {
        using var _ = await LifecycleLease.AcquireAsync(Root, wait, TimeProvider.System, ct);
    }
}
