using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonRunnerMuseConfigTests {
    [Test]
    public async Task Muse_path_and_model_variables_reach_the_daemon_config() {
        var config = new DaemonConfig();
        var env    = new Dictionary<string, string> { ["KCAP_MUSE_PATH"] = "/x/muse", ["KCAP_MUSE_MODEL"] = "muse-wonder-1.0" };

        DaemonRunner.BindVendorOverrides(config, [HarnessId.Muse], name => env.GetValueOrDefault(name));

        await Assert.That(config.MusePath).IsEqualTo("/x/muse");
        await Assert.That(config.MuseModel).IsEqualTo("muse-wonder-1.0");
    }

    [Test]
    public async Task The_unattended_reviewer_is_on_by_default_and_the_consent_applier_reaches_its_flag() {
        var config = new DaemonConfig();
        await Assert.That(config.MuseUnattendedReviewerEnabled).IsTrue();

        DaemonRunner.ConsentApplier(config, DaemonRunner.MuseVendor)(false);

        await Assert.That(config.MuseUnattendedReviewerEnabled).IsFalse();
    }
}
