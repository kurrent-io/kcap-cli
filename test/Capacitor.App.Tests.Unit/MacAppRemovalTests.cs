using System.Runtime.Versioning;
using Capacitor.App.Platform.Mac;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;
using NSubstitute;
using Velopack.Locators;

namespace Capacitor.App.Tests.Unit;

public class MacAppRemovalTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class NoProcesses : IProcessRunner {
        public int Calls;
        public List<string[]> Seen { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            Seen.Add([fileName, .. args]);
            return Task.FromResult(new ProcessResult(1, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) {
            Calls++;
            return Task.FromResult(new StreamingResult(1, false, []));
        }
    }

    string Bundle() {
        var bundle = Tmp.GetResolvedPath("kcap.app");
        Tmp.CreateDir(Path.Combine("kcap.app", "Contents", "MacOS"));
        Tmp.CreateFile(Path.Combine("kcap.app", "Contents", "MacOS", "kcap"), "");
        Tmp.CreateFile(Path.Combine("kcap.app", "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
              <key>CFBundleName</key>
              <string>kcap</string>
              <key>CFBundleIdentifier</key>
              <string>io.kurrent.kcap</string>
            </dict>
            </plist>
            """);
        return bundle;
    }

    [Test, RunOn(OS.MacOs), UnsupportedOSPlatform("windows")]
    public async Task Removes_only_links_into_the_bundle() {
        var bundle = Bundle();
        var other  = Tmp.CreateFile("other-kcap", "");
        Tmp.CreateDir("ours");
        Tmp.CreateDir("theirs");
        Tmp.CreateFile(Path.Combine("plain", "kcap"), "");
        var ours   = Tmp.GetResolvedPath("ours");
        var theirs = Tmp.GetResolvedPath("theirs");
        var plain  = Tmp.GetResolvedPath("plain");
        File.CreateSymbolicLink(Path.Combine(ours, "kcap"), Path.Combine(bundle, "Contents", "MacOS", "kcap"));
        File.CreateSymbolicLink(Path.Combine(theirs, "kcap"), other);
        var runner  = new NoProcesses();
        var removal = new MacAppRemoval(runner, new UserHome(Tmp.PathTo("home")), bundle, [ours, theirs, plain]);

        var left = await removal.RemoveLinksAsync(CancellationToken.None);

        await Assert.That(left).IsEmpty();
        await Assert.That(new FileInfo(Path.Combine(ours, "kcap")).LinkTarget).IsNull();
        await Assert.That(File.Exists(Path.Combine(ours, "kcap"))).IsFalse();
        await Assert.That(new FileInfo(Path.Combine(theirs, "kcap")).LinkTarget).IsEqualTo(other);
        await Assert.That(File.Exists(Path.Combine(plain, "kcap"))).IsTrue();
        await Assert.That(runner.Calls).IsEqualTo(0);
    }

    [Test, RunOn(OS.MacOs), UnsupportedOSPlatform("windows")]
    public async Task Removes_the_profile_line_with_the_local_bin_link() {
        var bundle = Bundle();
        Tmp.CreateDir("home", ".local", "bin");
        var home    = new UserHome(Tmp.GetResolvedPath("home"));
        var folder  = ShellProfile.Folder(home);
        var profile = Tmp.CreateFile(Path.Combine("home", ".zprofile"), "export A=1\n");
        new ShellProfile(home, "/bin/zsh").Add();
        File.CreateSymbolicLink(Path.Combine(folder, "kcap"), Path.Combine(bundle, "Contents", "MacOS", "kcap"));
        var removal = new MacAppRemoval(new NoProcesses(), home, bundle, [folder]);

        var left = await removal.RemoveLinksAsync(CancellationToken.None);

        await Assert.That(left).IsEmpty();
        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1\n");
    }

    [Test]
    public async Task Deletes_support_files_named_by_the_bundle_id() {
        var bundle  = Bundle();
        var library = Path.Combine("home", "Library");
        var caches  = Tmp.CreateDir(Path.Combine(library, "Caches", "io.kurrent.kcap"));
        Tmp.CreateFile(Path.Combine(library, "Caches", "io.kurrent.kcap", "blob"), "x");
        var http    = Tmp.CreateDir(Path.Combine(library, "HTTPStorages", "io.kurrent.kcap"));
        var saved   = Tmp.CreateDir(Path.Combine(library, "Saved Application State", "io.kurrent.kcap.savedState"));
        var prefs   = Tmp.CreateFile(Path.Combine(library, "Preferences", "io.kurrent.kcap.plist"), "");
        var unrelated = Tmp.CreateFile(Path.Combine(library, "Preferences", "com.example.other.plist"), "");
        var removal = new MacAppRemoval(new NoProcesses(), new UserHome(Tmp.PathTo("home")), bundle, []);

        await Assert.That(removal.DeleteSupportFiles()).IsTrue();

        await Assert.That(Directory.Exists(caches)).IsFalse();
        await Assert.That(Directory.Exists(http)).IsFalse();
        await Assert.That(Directory.Exists(saved)).IsFalse();
        await Assert.That(File.Exists(prefs)).IsFalse();
        await Assert.That(File.Exists(unrelated)).IsTrue();
    }

    [Test]
    public async Task A_bundle_without_an_identifier_reports_its_support_files_as_left() {
        var removal = new MacAppRemoval(new NoProcesses(), new UserHome(Tmp.PathTo("home")), Tmp.CreateDir("Bare.app"), []);

        await Assert.That(removal.DeleteSupportFiles()).IsFalse();
    }

    [Test]
    public async Task Removes_updater_files_for_this_bundle_and_preserves_other_apps() {
        var bundle = Bundle();
        var home = new UserHome(Tmp.GetResolvedPath("home"));
        var packages = Tmp.CreateDir("home/Library/Caches/velopack/kcap/packages");
        var scratch = Tmp.CreateDir("temp/velopack/kcap");
        var otherScratch = Tmp.CreateDir("temp/velopack/other");
        var log = Tmp.CreateFile("home/Library/Logs/velopack_kcap.log", "log");
        var tempLog = Tmp.CreateFile("temp/velopack_kcap.log", "log");
        var other = Tmp.CreateFile("home/Library/Caches/velopack/other/packages/release.nupkg", "keep");
        var locator = Substitute.For<IVelopackLocator>();
        locator.RootAppDir.Returns(bundle);
        locator.AppId.Returns("kcap");
        locator.PackagesDir.Returns(packages);
        locator.AppTempDir.Returns(scratch);
        var files = MacUpdaterFiles.Resolve(locator, bundle, home, Tmp.PathTo("temp"));
        var removal = new MacAppRemoval(new NoProcesses(), home, bundle, [], files);

        removal.DeleteSupportFiles();

        await Assert.That(Directory.Exists(Path.GetDirectoryName(packages))).IsFalse();
        await Assert.That(Directory.Exists(scratch)).IsFalse();
        await Assert.That(Directory.Exists(otherScratch)).IsTrue();
        await Assert.That(File.Exists(log)).IsFalse();
        await Assert.That(File.Exists(tempLog)).IsFalse();
        await Assert.That(File.Exists(other)).IsTrue();
        await Assert.That(MacUpdaterFiles.Resolve(locator, Tmp.PathTo("other.app"), home, Tmp.PathTo("temp"))).IsNull();
    }

    [Test, RunOn(OS.MacOs), UnsupportedOSPlatform("windows")]
    public async Task Repointed_link_never_reaches_the_admin_prompt() {
        var bundle = Bundle();
        var other  = Tmp.CreateFile("other-kcap", "");
        Tmp.CreateDir("bin");
        var link   = Path.Combine(Tmp.GetResolvedPath("bin"), "kcap");
        File.CreateSymbolicLink(link, other);
        var runner  = new NoProcesses();
        var removal = new MacAppRemoval(runner, new UserHome(Tmp.PathTo("home")), bundle, []);

        await removal.RemoveAsAdminAsync([new KcapLinkFailure(link, "Permission denied")], CancellationToken.None);

        await Assert.That(runner.Calls).IsEqualTo(0);
    }

    [Test, RunOn(OS.MacOs), UnsupportedOSPlatform("windows")]
    public async Task Undeletable_link_into_the_bundle_asks_for_admin() {
        var bundle = Bundle();
        Tmp.CreateDir("locked");
        var folder = Tmp.GetResolvedPath("locked");
        var link   = Path.Combine(folder, "kcap");
        var binary = Path.Combine(bundle, "Contents", "MacOS", "kcap");
        File.CreateSymbolicLink(link, binary);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            // Root deletes from a read-only folder, so the plain delete would not fail.
            try {
                File.WriteAllText(Path.Combine(folder, "probe"), "");
                return;
            } catch (UnauthorizedAccessException) { }

            var runner  = new NoProcesses();
            var removal = new MacAppRemoval(runner, new UserHome(Tmp.PathTo("home")), bundle, [folder]);

            var left = await removal.RemoveLinksAsync(CancellationToken.None);

            await Assert.That(runner.Calls).IsEqualTo(1);
            await Assert.That(runner.Seen[0][0]).IsEqualTo("/usr/bin/osascript");
            await Assert.That(runner.Seen[0][^2..]).IsEquivalentTo(new[] { link, binary }, CollectionOrdering.Matching);
            await Assert.That(left).IsEquivalentTo(new[] { link });
        } finally {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    [Arguments("io.kurrent.kcap", true)]
    [Arguments("io.kurrent-dev.kcap2", true)]
    [Arguments("kcap", false)]
    [Arguments(".", false)]
    [Arguments("..", false)]
    [Arguments("io..kcap", false)]
    [Arguments("io.kurrent.", false)]
    [Arguments("../io.kcap", false)]
    [Arguments("io/kurrent.kcap", false)]
    [Arguments("io.kurrent kcap", false)]
    [Arguments("", false)]
    public async Task Bundle_id_must_be_reverse_dns(string id, bool accepted) =>
        await Assert.That(MacAppRemoval.IsReverseDns(id)).IsEqualTo(accepted);
}
