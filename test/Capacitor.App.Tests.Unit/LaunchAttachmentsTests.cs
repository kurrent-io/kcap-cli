using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The remote daemon gate: a launch only carries attachment ids to a daemon whose build fails the
/// launch closed when an id cannot be fetched.
public class LaunchAttachmentsTests {
    [Test]
    [Arguments("1.0.3", true)]
    [Arguments("1.0.3-alpha.2", true)]
    [Arguments("1.0.4+build.7", true)]
    [Arguments("2.0.0", true)]
    [Arguments("1.0.2", false)]
    [Arguments("garbage", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Is_capable_reads_the_semver_core_and_refuses_what_it_cannot_parse(string? version, bool expected) {
        await Assert.That(LaunchAttachments.IsCapable(version)).IsEqualTo(expected);
    }
}
