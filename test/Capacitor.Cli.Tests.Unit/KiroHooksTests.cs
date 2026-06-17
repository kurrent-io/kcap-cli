using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the Kiro agent-hooks writer (<see cref="PluginCommand.InstallKiroHooks"/> /
/// <see cref="PluginCommand.RemoveKiroHooks"/>), the installer marker helpers,
/// and the parser. Like Copilot, kcap owns its own agent file
/// (<c>~/.kiro/agents/kcap.json</c>) wholesale — install is a write, remove is a
/// delete.
/// </summary>
public class KiroHooksTests {
    [Test]
    public async Task fresh_install_writes_agentspawn_hook_with_embedded_event_and_timeout() {
        using var tmp = new TempDir();
        var agentPath = Path.Combine(tmp.Path, "agents", "kcap.json");

        var ok = PluginCommand.InstallKiroHooks(agentPath);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(agentPath))!.AsObject();
        var hooks = root["hooks"]!.AsObject();

        foreach (var evt in KiroHooksParser.KiroHookEvents) {
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);

            var entry = entries[0]!.AsObject();
            await Assert.That(entry["command"]!.GetValue<string>()).IsEqualTo($"kcap hook --kiro --event {evt}");
            await Assert.That(entry["timeout_ms"]!.GetValue<int>()).IsEqualTo(5000);
        }

        // agentSpawn is the only subscribed event — see KiroHooksParser.
        await Assert.That(KiroHooksParser.KiroHookEvents).Contains("agentSpawn");
        await Assert.That(KiroHooksParser.KiroHookEvents.Contains("stop")).IsFalse();
    }

    [Test]
    public async Task install_writes_marker_and_remove_deletes_file_and_marker() {
        using var tmp = new TempDir();
        var agentPath = Path.Combine(tmp.Path, "agents", "kcap.json");

        PluginCommand.InstallKiroHooks(agentPath);

        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsTrue();
        await Assert.That(KiroHooksInstaller.ReadMarker(agentPath)).IsNotNull();

        var removed = PluginCommand.RemoveKiroHooks(agentPath);

        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(agentPath)).IsFalse();
        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsFalse();
        await Assert.That(KiroHooksInstaller.ReadMarker(agentPath)).IsNull();
    }

    [Test]
    public async Task remove_without_install_reports_nothing_removed() {
        using var tmp = new TempDir();
        var agentPath = Path.Combine(tmp.Path, "agents", "kcap.json");

        await Assert.That(PluginCommand.RemoveKiroHooks(agentPath)).IsFalse();
    }

    [Test]
    public async Task is_installed_detects_pre_marker_installs_from_file_content() {
        using var tmp = new TempDir();
        var agentsDir = Path.Combine(tmp.Path, "agents");
        var agentPath = Path.Combine(agentsDir, "kcap.json");

        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(agentPath, """
            {"name":"kcap","hooks":{"agentSpawn":[{"command":"kcap hook --kiro --event agentSpawn"}]}}
        """);

        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsTrue();
    }

    [Test]
    public async Task parser_matches_kiro_command_only() {
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"kcap hook --kiro --event agentSpawn"}"""))).IsTrue();
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"kcap hook --copilot --event sessionStart"}"""))).IsFalse();
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"/usr/local/bin/other"}"""))).IsFalse();
    }

    [Test]
    public async Task has_capacitor_hooks_for_requires_every_event() {
        using var tmp = new TempDir();
        var agentPath = Path.Combine(tmp.Path, "agents", "kcap.json");

        PluginCommand.InstallKiroHooks(agentPath);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(agentPath))!.AsObject();

        await Assert.That(KiroHooksParser.HasCapacitorHooksFor(root, KiroHooksParser.KiroHookEvents)).IsTrue();
        await Assert.That(KiroHooksParser.HasCapacitorHooksFor(root, ["agentSpawn", "someFutureEvent"])).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-kiro-hooks-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
