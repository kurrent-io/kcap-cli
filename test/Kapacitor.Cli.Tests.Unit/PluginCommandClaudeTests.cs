using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandClaudeTests {
    [Test]
    public async Task InstallPlugin_stamps_marker_on_success() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        var ok = SetupCommand.InstallPlugin(settingsPath, "/some/marketplace");
        await Assert.That(ok).IsTrue();

        var marker = Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(marker)).Trim())
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_no_marker_and_no_entries() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            var settingsPath = Path.Combine(fakeHome.FullName, ".claude", "settings.json");
            await Assert.That(File.Exists(settingsPath)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_claude_with_if_installed_refreshes_pre_marker_install() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalPlug = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
        var pluginDir    = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        try {
            // Seed pre-marker install: enabledPlugins entry, no marker.
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/old/path" } } },
                  "enabledPlugins": { "kapacitor@kapacitor": true }
                }
                """);

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginDir.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // Marketplace path must now point at the new plugin dir.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            var path = root["extraKnownMarketplaces"]!["kapacitor"]!["source"]!["path"]!.GetValue<string>();
            await Assert.That(path).IsEqualTo(pluginDir.FullName);

            // Marker stamped.
            await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginDir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_marker_matches_current_version() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            // Sentinel content + matching marker.
            await File.WriteAllTextAsync(settingsPath, """{"sentinel": "must-survive"}""");
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                KapacitorVersion.Current());
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            await Assert.That(root["sentinel"]!.GetValue<string>()).IsEqualTo("must-survive");
            await Assert.That(root["enabledPlugins"]).IsNull();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    [NotInParallel("ConsoleStreams")]
    public async Task Install_claude_with_if_installed_swallows_plugin_resolution_failure() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalPlug = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
        var originalErr  = Console.Error;
        var capturedErr  = new StringWriter();
        try {
            // Seed: marker present so the gate proceeds…
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                "some-old-version");

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            // …but plugin dir resolution fails.
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR",
                Path.Combine(Path.GetTempPath(), $"kapacitor-missing-{Guid.NewGuid():N}"));
            Console.SetError(capturedErr);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capturedErr.ToString()).IsEmpty();
        } finally {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Remove_claude_deletes_marker() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/p" } } },
                  "enabledPlugins": { "kapacitor@kapacitor": true }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                KapacitorVersion.Current());

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "remove"]);
            await Assert.That(exit).IsEqualTo(0);

            await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-claude-plugin-cmd-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
