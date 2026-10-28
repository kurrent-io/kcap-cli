using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.Tests.Unit.Install;

/// <summary>A binary belongs to a snap only under <c>/snap/&lt;name&gt;/&lt;revision&gt;/</c>; Stabilize maps that to
/// <c>current</c> so a persisted path survives snapd removing the revision.</summary>
public class SnapLayoutTests {
    static Func<string, bool> Existing(params string[] paths) => paths.Contains;

    [Test]
    [Arguments("/snap/kurrent-capacitor/42/bin/kcap", "kurrent-capacitor", "42")]
    [Arguments("/snap/kurrent-capacitor/x3/bin/kcap-daemon", "kurrent-capacitor", "x3")]
    public async Task A_binary_inside_a_revision_belongs_to_the_snap(string path, string name, string revision) {
        await Assert.That(SnapLayout.FromBinary(path)).IsEqualTo(new SnapLayout(name, revision));
    }

    [Test]
    [Arguments("/snap/kurrent-capacitor/current/bin/kcap")]
    [Arguments("/snap/bin/kcap")]
    [Arguments("/snap/kurrent-capacitor/42")]
    [Arguments("/snap/kurrent-capacitor/x/bin/kcap")]
    [Arguments("/home/u/snap/kurrent-capacitor/42/bin/kcap")]
    [Arguments("/usr/local/bin/kcap")]
    public async Task Any_other_path_is_not_a_snap_revision(string path) {
        await Assert.That(SnapLayout.FromBinary(path)).IsNull();
    }

    [Test]
    public async Task Stabilize_maps_a_revision_path_to_current() {
        var stable = SnapLayout.Stabilize("/snap/kurrent-capacitor/42/bin/kcap-daemon", Existing("/snap/kurrent-capacitor/current/bin/kcap-daemon"));

        await Assert.That(stable).IsEqualTo("/snap/kurrent-capacitor/current/bin/kcap-daemon");
    }

    /// <summary>Without a <c>current</c> link to name, the revision path is the only one that runs.</summary>
    [Test]
    public async Task Stabilize_keeps_the_revision_path_when_current_does_not_exist() {
        var stable = SnapLayout.Stabilize("/snap/kurrent-capacitor/42/bin/kcap", _ => false);

        await Assert.That(stable).IsEqualTo("/snap/kurrent-capacitor/42/bin/kcap");
    }

    [Test]
    public async Task InstallPaths_stabilizes_a_snap_path() {
        var stable = InstallPaths.Stabilize("/snap/kurrent-capacitor/42/bin/kcap", Existing("/snap/kurrent-capacitor/current/bin/kcap"));

        await Assert.That(stable).IsEqualTo("/snap/kurrent-capacitor/current/bin/kcap");
    }
}
