using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": false } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsClaudePlugin_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_when_kcap_command_present() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_no_kcap_command() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    // Fix #2: non-string command field should not throw — treated as not-installed.
    [Test]
    public async Task DetectsCodexHooks_returns_false_for_numeric_command_field() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] }
                ]
              }
            }
            """);

        // Must not throw; numeric command is not a kcap entry.
        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
