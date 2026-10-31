namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The sentence a replace offer carries about the daemon's version: a downgrade is named as one, and
/// an unknown version or the same release says nothing rather than guess.
/// </summary>
public class DaemonVersionChangeTests {
    [Test]
    public async Task A_newer_daemon_replaced_by_an_older_kcap_is_called_a_downgrade() {
        var text = DaemonVersionChange.Describe("1.4.1-beta.1+abc", "1.4.0+def");

        await Assert.That(text).IsEqualTo(
            "The daemon runs 1.4.1-beta.1, newer than this kcap's 1.4.0: replacing it now downgrades it to 1.4.0. "
          + "Update this kcap to 1.4.1-beta.1 or later first to keep the newer daemon.");
        await Assert.That(DaemonVersionChange.IsDowngrade("1.4.1-beta.1", "1.4.0")).IsTrue();
    }

    [Test]
    public async Task An_upgrade_names_both_versions() {
        await Assert.That(DaemonVersionChange.Describe("1.4.0", "1.4.1-beta.1"))
            .IsEqualTo("The daemon runs 1.4.0; replacing it installs 1.4.1-beta.1.");
        await Assert.That(DaemonVersionChange.IsDowngrade("1.4.0", "1.4.1-beta.1")).IsFalse();
    }

    /// <summary>A prerelease sorts below its release, so the release replacing it is an upgrade.</summary>
    [Test]
    public async Task A_release_replacing_its_own_beta_is_an_upgrade() {
        await Assert.That(DaemonVersionChange.IsDowngrade("1.4.0-beta.3", "1.4.0")).IsFalse();
        await Assert.That(DaemonVersionChange.IsDowngrade("1.4.0", "1.4.0-beta.3")).IsTrue();
    }

    [Test]
    [Arguments(null, "1.4.0")]
    [Arguments("1.4.0", null)]
    [Arguments("unknown", "1.4.0")]
    [Arguments("1.4.0+abc", "1.4.0+def")]
    public async Task Unknown_or_same_release_says_nothing(string? running, string? target) {
        await Assert.That(DaemonVersionChange.Describe(running, target)).IsNull();
    }
}
