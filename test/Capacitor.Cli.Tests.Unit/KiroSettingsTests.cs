using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers <see cref="KiroSettings"/>: the <c>chat.defaultAgent</c> flip kcap uses
/// to make its cloned agent the launch default (so hooks fire for every session),
/// and restore-on-remove. Other settings keys must survive the round-trip.
/// </summary>
public class KiroSettingsTests {
    [Test]
    public async Task read_default_agent_is_null_when_file_absent() {
        using var tmp = new TempDir();
        await Assert.That(KiroSettings.ReadDefaultAgent(Path.Combine(tmp.Path, "cli.json"))).IsNull();
    }

    [Test]
    public async Task set_default_creates_file_and_read_round_trips() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings", "cli.json");

        await Assert.That(KiroSettings.SetDefaultAgent(settingsPath, "kcap")).IsTrue();
        await Assert.That(KiroSettings.ReadDefaultAgent(settingsPath)).IsEqualTo("kcap");
    }

    [Test]
    public async Task set_default_preserves_other_keys() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "cli.json");
        await File.WriteAllTextAsync(settingsPath,
            """{"chat.defaultModel":"minimax-m2.5","chat.defaultAgent":"kiro_default","other.flag":true}""");

        await Assert.That(KiroSettings.SetDefaultAgent(settingsPath, "kcap")).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        await Assert.That(root["chat.defaultAgent"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(root["chat.defaultModel"]!.GetValue<string>()).IsEqualTo("minimax-m2.5");
        await Assert.That(root["other.flag"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task install_then_restore_round_trip() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "cli.json");
        await File.WriteAllTextAsync(settingsPath, """{"chat.defaultAgent":"kiro_default"}""");

        // Install: capture the prior default, flip to kcap.
        var prior = KiroSettings.ReadDefaultAgent(settingsPath);
        await Assert.That(prior).IsEqualTo("kiro_default");
        KiroSettings.SetDefaultAgent(settingsPath, "kcap");
        await Assert.That(KiroSettings.ReadDefaultAgent(settingsPath)).IsEqualTo("kcap");

        // Remove: restore the prior default.
        KiroSettings.SetDefaultAgent(settingsPath, prior!);
        await Assert.That(KiroSettings.ReadDefaultAgent(settingsPath)).IsEqualTo("kiro_default");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-kiro-settings-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
