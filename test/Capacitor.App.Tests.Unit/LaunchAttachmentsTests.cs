using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The remote daemon gate: a launch only carries attachment ids to a daemon whose build fails the
/// launch closed when an id cannot be fetched.
public class LaunchAttachmentsTests {
    [Test]
    [Arguments("1.0.4", true)]
    [Arguments("1.0.4-alpha.2", true)]
    [Arguments("1.0.5+build.7", true)]
    [Arguments("2.0.0", true)]
    [Arguments("1.0.3", false)]
    [Arguments("garbage", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Is_capable_reads_the_semver_core_and_refuses_what_it_cannot_parse(string? version, bool expected) {
        await Assert.That(LaunchAttachments.IsCapable(version)).IsEqualTo(expected);
    }

    [Test]
    public async Task The_minimum_is_the_release_whose_daemon_fails_closed_on_a_missing_attachment() {
        await Assert.That(LaunchAttachments.MinDaemonVersion).IsEqualTo(new Version(1, 0, 4));
    }
}
