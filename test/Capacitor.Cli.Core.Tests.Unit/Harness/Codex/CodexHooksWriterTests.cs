using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexHooksWriterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Install_adds_a_kcap_hook_for_every_event() {
        var hooks = Tmp.PathTo("hooks.json");

        await Assert.That(CodexHooksWriter.Install(hooks)).IsEqualTo(SettingsEdit.Changed);

        var root = JsonNode.Parse(File.ReadAllText(hooks))!["hooks"]!;
        foreach (var evt in CodexHooksParser.CodexHookEvents) {
            var entries = root[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);
            await Assert.That(entries[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo(CodexHooksWriter.HookCommand);
        }
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(hooks)).IsTrue();
    }

    [Test]
    public async Task Install_keeps_foreign_hooks_and_replaces_its_own() {
        var hooks = Tmp.CreateFile("hooks.json", """
            { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "my-tool" } ] },
                                    { "hooks": [ { "type": "command", "command": "kcap hook --codex" } ] } ] } }
            """);

        CodexHooksWriter.Install(hooks);

        var stop = JsonNode.Parse(File.ReadAllText(hooks))!["hooks"]!["Stop"]!.AsArray();
        await Assert.That(stop.Count).IsEqualTo(2);
        await Assert.That(stop[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("my-tool");
    }

    [Test]
    public async Task Install_refuses_malformed_hooks() {
        var hooks = Tmp.CreateFile("hooks.json", "nope");

        await Assert.That(CodexHooksWriter.Install(hooks)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(hooks)).IsEqualTo("nope");
    }

    [Test]
    public async Task Install_replaces_a_non_object_hooks_value_without_throwing() {
        var hooks = Tmp.CreateFile("hooks.json", """{"hooks":"x"}""");

        await Assert.That(CodexHooksWriter.Install(hooks)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(hooks)).IsTrue();
    }

    [Test]
    public async Task Remove_drops_only_kcap_entries() {
        var hooks = Tmp.PathTo("hooks.json");
        CodexHooksWriter.Install(hooks);

        await Assert.That(CodexHooksWriter.Remove(hooks)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(hooks)).IsFalse();
    }
}
