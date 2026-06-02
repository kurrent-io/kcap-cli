using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit;

public class CursorHooksWriterTests {
    [Test]
    public async Task fresh_install_writes_all_eight_events() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        foreach (var evt in new[] {
            "sessionStart", "sessionEnd", "beforeSubmitPrompt",
            "afterAgentResponse", "afterAgentThought",
            "preToolUse", "postToolUse", "postToolUseFailure"
        }) {
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsGreaterThanOrEqualTo(1);
            var cmd = entries[0]!["command"]!.GetValue<string>();
            await Assert.That(cmd).IsEqualTo("kapacitor hook --cursor");
        }
    }

    [Test]
    public async Task install_preserves_user_authored_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"/usr/local/bin/other"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var start = root["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "/usr/local/bin/other")).IsTrue();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "kapacitor hook --cursor")).IsTrue();
    }

    [Test]
    public async Task install_replaces_existing_kapacitor_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kapacitor hook --cursor --legacy"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var start = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!
            .AsObject()["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(start.Count(e => e!["command"]!.GetValue<string>().Contains("kapacitor hook --cursor")))
            .IsEqualTo(1);
        await Assert.That(start.Single(e => e!["command"]!.GetValue<string>().Contains("kapacitor hook --cursor"))!["command"]!.GetValue<string>())
            .IsEqualTo("kapacitor hook --cursor");
    }

    [Test]
    public async Task install_stamps_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kapacitor-hooks-version"))).IsTrue();
    }

    [Test]
    public async Task remove_strips_kapacitor_entries_and_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        var removed = PluginCommand.RemoveCursorHooks(hooksPath);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kapacitor-hooks-version"))).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-writer-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
