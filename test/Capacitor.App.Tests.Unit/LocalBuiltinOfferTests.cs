using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

public class LocalBuiltinOfferTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// Mirrors the CLI: a re-run asks about the installed stack, a first install about the version this build installs.
    [Test]
    public async Task The_target_is_the_installed_version_else_the_one_this_build_installs() {
        var root = new LocalRoot(Tmp.CreateDir("local"));

        await Assert.That(LocalBuiltinOffer.TargetVersion(root)).IsEqualTo(LocalBundleClient.RequestedVersion());

        LocalStateFiles.PublishRegistry(root, new InstallRegistry(new LocalInstallation("inst-1", 51234, "http://localhost:51234", "1.4.0"), []));

        await Assert.That(LocalBuiltinOffer.TargetVersion(root)).IsEqualTo("1.4.0");
    }
}
