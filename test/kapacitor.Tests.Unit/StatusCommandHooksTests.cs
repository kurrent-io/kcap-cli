using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kapacitor@kapacitor": false } }
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
    public async Task DetectsCodexHooks_when_kapacitor_command_present() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_no_kapacitor_command() {
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

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
