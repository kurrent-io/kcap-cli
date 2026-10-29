using Capacitor.App.Services.Update;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class UpdateCoordinatorHandoverTests {
    static UpdateCandidate Beta3 => new("0.12.0-beta.3", IsPrerelease: true);

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task The_handover_is_recorded_once_just_before_the_updater_takes_over() {
        var updater = new FakeAppUpdater { NextCheck = Beta3 };
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var clock = new FakeTimeProvider();
        var quits = 0;
        var handovers = new List<(string Version, int AppliedBefore)>();
        var coordinator = new UpdateCoordinator(updater, surface, clock, () => quits++, CancellationToken.None,
            handingOver: candidate => handovers.Add((candidate.Version, updater.ApplyOnExitCalls.Count)));
        coordinator.Start();
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => quits == 1, "the quit request");

        await Assert.That(handovers).IsEmpty();
        coordinator.ApplyPendingOnExit();
        coordinator.ApplyPendingOnExit();

        await Assert.That(handovers.SequenceEqual([("0.12.0-beta.3", 0)])).IsTrue();
        await Assert.That(updater.ApplyOnExitCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_handover_that_cannot_be_recorded_still_applies_the_update() {
        var updater = new FakeAppUpdater { NextCheck = Beta3 };
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var clock = new FakeTimeProvider();
        var quits = 0;
        var coordinator = new UpdateCoordinator(updater, surface, clock, () => quits++, CancellationToken.None,
            handingOver: _ => throw new IOException("disk full"));
        coordinator.Start();
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => quits == 1, "the quit request");

        coordinator.ApplyPendingOnExit();

        await Assert.That(updater.ApplyOnExitCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_pending_package_applied_at_startup_is_handed_over_first() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };
        var handedOver = new List<string>();

        var applied = UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false,
            handingOver: candidate => { if (updater.ApplyNowCalls.Count == 0) handedOver.Add(candidate.Version); });

        await Assert.That(applied).IsTrue();
        await Assert.That(handedOver.SequenceEqual(["0.12.0-beta.3"])).IsTrue();
    }

    [Test]
    public async Task A_failed_download_is_reported_as_a_download_failure() {
        var updater = new FakeAppUpdater { NextCheck = Beta3, DownloadFailure = new HttpRequestException("reset") };
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var coordinator = new UpdateCoordinator(updater, surface, new FakeTimeProvider(), () => { }, CancellationToken.None);

        await coordinator.RunMenuActionAsync();

        await Assert.That(surface.Prompts.Single().Disclosure).IsEqualTo("The update could not be downloaded. Try again later.");
    }

    [Test]
    public async Task A_pending_package_still_applies_when_its_handover_cannot_be_recorded() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };

        var applied = UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false,
            handingOver: _ => throw new IOException("disk full"));

        await Assert.That(applied).IsTrue();
        await Assert.That(updater.ApplyNowCalls.Count).IsEqualTo(1);
    }
}
