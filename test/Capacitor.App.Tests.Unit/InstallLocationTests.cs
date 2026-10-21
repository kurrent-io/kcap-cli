using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationTests {
    const string Home = "/Users/dev";

    [Test]
    public async Task BundleRoot_is_the_app_ancestor_of_the_executable() {
        await Assert.That(InstallLocation.BundleRoot("/Applications/kcap.app/Contents/MacOS/kcap-desktop"))
            .IsEqualTo("/Applications/kcap.app");
    }

    [Test]
    [Arguments("/Users/dev/src/kcap-cli/src/Capacitor.App/bin/Debug/net10.0/kcap")]
    [Arguments("")]
    [Arguments(null)]
    public async Task BundleRoot_is_null_outside_a_bundle(string? path) {
        await Assert.That(InstallLocation.BundleRoot(path)).IsNull();
    }

    [Test]
    [Arguments("/Applications/kcap.app", InstallLocationKind.Applications)]
    [Arguments("/Users/dev/Applications/kcap.app", InstallLocationKind.UserApplications)]
    [Arguments("/Volumes/kcap/kcap.app", InstallLocationKind.DmgVolume)]
    [Arguments("/private/var/folders/xy/T/AppTranslocation/1F2E-3D4C/d/kcap.app", InstallLocationKind.Translocated)]
    [Arguments("/Users/dev/Downloads/kcap.app", InstallLocationKind.Other)]
    [Arguments(null, InstallLocationKind.NotABundle)]
    public async Task Classify_recognises_each_shape(string? root, InstallLocationKind expected) {
        await Assert.That(InstallLocation.Classify(root, Home)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(InstallLocationKind.NotABundle, true)]
    [Arguments(InstallLocationKind.Applications, true)]
    [Arguments(InstallLocationKind.UserApplications, true)]
    [Arguments(InstallLocationKind.DmgVolume, false)]
    [Arguments(InstallLocationKind.Translocated, false)]
    [Arguments(InstallLocationKind.Other, false)]
    public async Task Passes_only_for_installed_or_unbundled(InstallLocationKind kind, bool expected) {
        await Assert.That(InstallLocation.Passes(kind)).IsEqualTo(expected);
    }
}
