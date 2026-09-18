using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class FeedbackTrailerTests {
    [Test]
    public async Task Connected_daemon_reports_its_snapshot_version() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", "1.0.2", "1.0.3"))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp 1.0.2");

    [Test]
    public async Task Never_observed_daemon_falls_back_to_the_installed_cli() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", null, "1.0.3"))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp cli 1.0.3");

    [Test]
    public async Task No_version_anywhere_says_so() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", "", null))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp version unknown");
}
