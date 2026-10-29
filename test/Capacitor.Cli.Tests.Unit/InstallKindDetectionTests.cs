using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Which install a binary belongs to decides how it updates; the shapes are the real layouts
/// with one file probe each, so the same cases hold on the Windows CI leg.</summary>
public class InstallKindDetectionTests {
    static Func<string, bool> Existing(params string[] suffixes) =>
        p => suffixes.Any(s => p.Replace('\\', '/').EndsWith(s, StringComparison.Ordinal));

    [Test]
    public async Task A_script_install_is_recognised_from_its_version_directory() {
        var kind = InstallProvenance.Detect("/home/u/.local/share/kcap/versions/1.2.0/bin/kcap", Existing("/kcap/install.json"));

        await Assert.That(kind).IsEqualTo(InstallKind.Script);
    }

    [Test]
    public async Task A_flat_npm_install_is_recognised_by_its_launcher() {
        var kind = InstallProvenance.Detect(
            "/usr/local/lib/node_modules/@kurrent/kcap-linux-x64/bin/kcap", Existing("/node_modules/@kurrent/kcap/bin/kcap.js"));

        await Assert.That(kind).IsEqualTo(InstallKind.Npm);
    }

    [Test]
    public async Task A_nested_npm_install_is_recognised_by_its_launcher() {
        var kind = InstallProvenance.Detect(
            "/opt/homebrew/lib/node_modules/@kurrent/kcap/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap",
            Existing("/lib/node_modules/@kurrent/kcap/bin/kcap.js"));

        await Assert.That(kind).IsEqualTo(InstallKind.Npm);
    }

    [Test]
    public async Task The_app_bundle_wins() {
        var kind = InstallProvenance.Detect("/Applications/kcap.app/Contents/MacOS/kcap", Existing("/Contents/Info.plist"));

        await Assert.That(kind).IsEqualTo(InstallKind.App);
    }

    [Test]
    public async Task Anything_else_is_unknown() {
        await Assert.That(InstallProvenance.Detect("/usr/local/bin/kcap", _ => false)).IsEqualTo(InstallKind.Unknown);
        await Assert.That(InstallProvenance.Detect(null, _ => true)).IsEqualTo(InstallKind.Unknown);
    }

    [Test]
    public async Task The_reinstall_hint_follows_the_install() {
        await Assert.That(InstallProvenance.ReinstallCommand(InstallKind.Script, windows: false)).Contains("/install | bash");
        await Assert.That(InstallProvenance.ReinstallCommand(InstallKind.Script, windows: true)).Contains("install.ps1 | iex");
        await Assert.That(InstallProvenance.ReinstallCommand(InstallKind.Npm, windows: false)).IsEqualTo("npm install -g @kurrent/kcap");
        await Assert.That(InstallProvenance.ReinstallCommand(InstallKind.Snap, windows: false, new SnapLayout("kcap", "42")))
            .IsEqualTo("export PATH=\"/snap/bin:$PATH\"  # or reinstall: sudo snap remove kcap && sudo snap install kcap --classic");
    }

    /// <summary>A file-installed snap is replaced from a file; removing it first would leave nothing when the
    /// Store has no kcap to install.</summary>
    [Test]
    public async Task A_local_snap_is_reinstalled_from_a_file_without_removing_it_first() {
        var hint = InstallProvenance.ReinstallCommand(InstallKind.Snap, windows: false, new SnapLayout("kcap", "x3"));

        await Assert.That(hint).IsEqualTo("export PATH=\"/snap/bin:$PATH\"  # or reinstall: sudo snap install --dangerous --classic <file>.snap");
    }

    /// <summary>A snap is known by its mount path alone; nothing under it is probed.</summary>
    [Test]
    public async Task A_snap_is_recognised_from_its_revision_mount() {
        var kind = InstallProvenance.Detect("/snap/kurrent-capacitor/42/bin/kcap", _ => false);

        await Assert.That(kind).IsEqualTo(InstallKind.Snap);
    }
}
