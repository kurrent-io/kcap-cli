using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudePluginInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "does-not-exist", "settings.json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName),
            "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_enabledPlugins_has_kapacitor() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_marketplace_has_kapacitor() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_enabledPlugins_kurrent_key_present() {
        // Pre-rename installs used the "kapacitor@kurrent" key. The installer
        // and remover both treat it as a kapacitor-owned stale entry, so the
        // refresh gate must detect it too — otherwise users on a pre-marker
        // pre-rename config would never get migrated.
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kurrent": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_marketplace_kurrent_key_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kurrent": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_has_unrelated_keys_only() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "theme": "dark" }""");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_is_malformed() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{not json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settingsPath))
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        ClaudePluginInstaller.DeleteMarker(settingsPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        ClaudePluginInstaller.DeleteMarker(settingsPath);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-claude-plugin-installer-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
