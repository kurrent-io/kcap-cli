using Capacitor.App.Platform.Mac;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class MacAppRemovalTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class NoProcesses : IProcessRunner {
        public int Calls;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
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

    [Test]
    public async Task Removes_only_links_into_the_bundle() {
        if (!OperatingSystem.IsMacOS()) return;

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

        removal.DeleteSupportFiles();

        await Assert.That(Directory.Exists(caches)).IsFalse();
        await Assert.That(Directory.Exists(http)).IsFalse();
        await Assert.That(Directory.Exists(saved)).IsFalse();
        await Assert.That(File.Exists(prefs)).IsFalse();
        await Assert.That(File.Exists(unrelated)).IsTrue();
    }
}
