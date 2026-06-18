using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Plugin-dispatch tests for the Pi live-ingest extension (AI-886). Pi has no
/// hook file — its integration is the <c>~/.pi/agent/extensions/kcap.ts</c>
/// extension, installed by <c>kcap plugin install --pi</c>, refreshed via
/// <c>--if-installed</c>, removed by <c>kcap plugin remove --pi</c>. The npm
/// refresh path (refresh.js) runs the <c>--if-installed</c> form on every
/// upgrade, so it MUST no-op for users who never opted into Pi — these tests
/// guard that contract at the dispatch layer (the installer primitives
/// themselves are covered by PiExtensionInstallerTests).
/// </summary>
public class PluginCommandPiTests {
    [Test]
    public async Task Install_pi_with_if_installed_is_noop_when_not_installed() {
        using var fakeHome = new TempDir();

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // kcap.ts must NOT exist — the npm refresh must never force-install the
        // Pi extension onto a user who never opted in.
        var extPath = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions", "kcap.ts");
        await Assert.That(File.Exists(extPath)).IsFalse();
    }

    [Test]
    public async Task Install_pi_with_if_installed_refreshes_existing_extension() {
        using var fakeHome = new TempDir();

        // Seed a stale kcap.ts with NO version marker (pre-marker install). The
        // refresh path should rewrite it in place and stamp the version marker.
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "kcap.ts");
        await File.WriteAllTextAsync(extPath, "// stale extension body");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var body = await File.ReadAllTextAsync(extPath);
        await Assert.That(body).DoesNotContain("stale extension body");
        await Assert.That(File.Exists(Path.Combine(extDir, ".kcap-extension-version"))).IsTrue();
    }

    [Test]
    public async Task Remove_pi_deletes_extension_and_marker() {
        using var fakeHome = new TempDir();

        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "kcap.ts");
        var marker  = Path.Combine(extDir, ".kcap-extension-version");
        await File.WriteAllTextAsync(extPath, "export default function(pi){}");
        await File.WriteAllTextAsync(marker, "1.0.0");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--pi"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(extPath)).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-pi-plugin-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
