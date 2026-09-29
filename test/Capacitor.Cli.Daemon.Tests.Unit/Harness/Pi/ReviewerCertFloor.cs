using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// Shared by the live Pi certs in this project (<see cref="PiHostedRuntimeLiveCertTests"/>,
/// <see cref="PiReviewerLiveCertTests"/>): a cert certifies behaviour at or above its recorded floor.
/// Below it, the run fails with the remedy instead of skipping, because a skipped cert reads as a pass.
/// </summary>
internal static class ReviewerCertFloor {
    public static async Task RequireAtOrAboveFloorAsync(string installed, string floor) {
        if (floor == "pending") return;   // first run: nothing to compare against yet

        var decision = ReviewerVersionAffirmations.Decide(installed, floor);

        await Assert.That(decision).IsEqualTo(ReviewerVersionAffirmation.MeetsMinimum)
            .Because($"pi {installed} is below this cert's floor {floor}; upgrade pi. Any newer build is in scope.");
    }
}
