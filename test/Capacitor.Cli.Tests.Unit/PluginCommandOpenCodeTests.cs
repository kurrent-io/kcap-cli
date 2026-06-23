using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Plugin-dispatch tests for the OpenCode live-ingest plugin (AI-919): install
/// (+ <c>--if-installed</c> refresh) and remove via <c>kcap plugin --opencode</c>.
/// The npm refresh path (refresh.js) runs the <c>--if-installed</c> form on every
/// upgrade, so it MUST no-op for users who never opted in — these tests guard that
/// contract at the dispatch layer (the installer primitives themselves are covered
/// by OpenCodeExtensionInstallerTests). Uses <c>--opencode-plugin-path</c> to stay
/// isolated from the real ~/.config/opencode and any ambient XDG_CONFIG_HOME.
/// </summary>
public class PluginCommandOpenCodeTests {
    [Test]
    public async Task Install_opencode_with_if_installed_is_noop_when_not_installed() {
        using var tmp = new TempDir();
        var pluginPath = Path.Combine(tmp.Path, "plugins", "kcap.ts");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--opencode-plugin-path", pluginPath, "--if-installed"],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        // kcap.ts must NOT exist — the npm refresh must never force-install the
        // OpenCode plugin onto a user who never opted in.
        await Assert.That(File.Exists(pluginPath)).IsFalse();
    }

    [Test]
    public async Task Install_opencode_with_if_installed_refreshes_existing_plugin() {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "plugins");
        Directory.CreateDirectory(dir);
        var pluginPath = Path.Combine(dir, "kcap.ts");
        // Seed a stale kcap.ts with NO version marker (pre-marker install). The
        // refresh path should rewrite it in place and stamp the version marker.
        await File.WriteAllTextAsync(pluginPath, "// stale plugin body");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--opencode-plugin-path", pluginPath, "--if-installed"],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        var body = await File.ReadAllTextAsync(pluginPath);
        await Assert.That(body).DoesNotContain("stale plugin body");
        await Assert.That(body).Contains("export const KcapPlugin");
        await Assert.That(File.Exists(Path.Combine(dir, ".kcap-extension-version"))).IsTrue();
    }

    [Test]
    public async Task Remove_opencode_deletes_plugin_and_marker() {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "plugins");
        Directory.CreateDirectory(dir);
        var pluginPath = Path.Combine(dir, "kcap.ts");
        var marker  = Path.Combine(dir, ".kcap-extension-version");
        await File.WriteAllTextAsync(pluginPath, "export const KcapPlugin = async () => ({})");
        await File.WriteAllTextAsync(marker, "1.0.0");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--opencode", "--opencode-plugin-path", pluginPath],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(pluginPath)).IsFalse();
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
            $"kcap-oc-plugincmd-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
