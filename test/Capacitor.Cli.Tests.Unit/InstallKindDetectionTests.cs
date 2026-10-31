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
    public async Task The_windows_app_install_is_recognised_beside_the_desktop_exe() {
        var kind = InstallProvenance.Detect(
            "C:/Users/u/AppData/Local/kcap/current/kcap.exe", Existing("/Local/kcap/Update.exe", "/kcap/current/kcap-desktop.exe"));

        await Assert.That(kind).IsEqualTo(InstallKind.App);
    }

    /// <summary>A folder named <c>current</c> is the app's only with Velopack's root above it and the app beside it.</summary>
    [Test]
    public async Task A_current_folder_without_the_app_is_not_the_app() {
        await Assert.That(InstallProvenance.Detect("C:/tools/current/kcap.exe", Existing("/current/kcap-desktop.exe")))
            .IsEqualTo(InstallKind.Unknown);
        await Assert.That(InstallProvenance.Detect("C:/tools/current/kcap.exe", Existing("/tools/Update.exe")))
            .IsEqualTo(InstallKind.Unknown);
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

    /// <summary>The app puts its own CLI on PATH; an npm reinstall would add a second, separate CLI.</summary>
    [Test]
    public async Task The_app_cli_off_path_is_pointed_at_the_app_path_fix_not_npm() {
        const string exe = @"C:\Users\u\AppData\Local\kcap\current\kcap.exe";

        var hint = InstallProvenance.NotOnPathHint(InstallKind.App, windows: true, exe);

        await Assert.That(hint).IsEqualTo(
            "Put the desktop app's kcap on PATH: choose \"Install command-line tool\" from the kcap icon in the system tray, "
          + $"or run: {exe} daemon shim ensure. Then open a new terminal.");
    }

    [Test]
    public async Task The_mac_app_hint_names_the_menu_bar() {
        var hint = InstallProvenance.NotOnPathHint(InstallKind.App, windows: false, "/Applications/kcap.app/Contents/MacOS/kcap");

        await Assert.That(hint).Contains("from the kcap icon in the menu bar");
        await Assert.That(hint).Contains("or run: /Applications/kcap.app/Contents/MacOS/kcap daemon shim ensure.");
        await Assert.That(hint).DoesNotContain("npm");
    }

    /// <summary>PowerShell runs a quoted path only behind its call operator.</summary>
    [Test]
    public async Task An_app_path_with_spaces_is_quoted_for_the_shell() {
        var windows = InstallProvenance.NotOnPathHint(InstallKind.App, windows: true, @"C:\Users\Jane Doe\AppData\Local\kcap\current\kcap.exe");
        var mac     = InstallProvenance.NotOnPathHint(InstallKind.App, windows: false, "/Users/jane/My Apps/kcap.app/Contents/MacOS/kcap");

        await Assert.That(windows).Contains(@"or run: & ""C:\Users\Jane Doe\AppData\Local\kcap\current\kcap.exe"" daemon shim ensure.");
        await Assert.That(mac).Contains(@"or run: ""/Users/jane/My Apps/kcap.app/Contents/MacOS/kcap"" daemon shim ensure.");
    }

    [Test]
    public async Task Without_a_process_path_the_app_hint_names_only_the_tray_item() {
        var hint = InstallProvenance.NotOnPathHint(InstallKind.App, windows: true, processPath: null);

        await Assert.That(hint).Contains("\"Install command-line tool\"");
        await Assert.That(hint).DoesNotContain("daemon shim ensure");
    }

    [Test]
    public async Task Other_installs_off_path_are_still_told_to_reinstall() {
        await Assert.That(InstallProvenance.NotOnPathHint(InstallKind.Npm, windows: true, "C:/npm/kcap.exe"))
            .IsEqualTo("Re-install kcap: npm install -g @kurrent/kcap");
        await Assert.That(InstallProvenance.NotOnPathHint(InstallKind.Script, windows: true, "C:/kcap/current/bin/kcap.exe"))
            .IsEqualTo("Re-install kcap: irm https://www.kurrent.io/install.ps1 | iex");
    }

    [Test]
    public async Task A_damaged_app_install_is_reinstalled_as_the_app() {
        await Assert.That(InstallProvenance.ReinstallHint(InstallKind.App, windows: true)).IsEqualTo("Re-install the kcap desktop app.");
        await Assert.That(InstallProvenance.ReinstallHint(InstallKind.Npm, windows: false)).IsEqualTo("Re-install kcap: npm install -g @kurrent/kcap");
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
