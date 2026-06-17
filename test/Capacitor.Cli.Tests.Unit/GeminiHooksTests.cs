using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-887: kcap merges its command hooks into Gemini's SHARED
/// <c>~/.gemini/settings.json</c> (nested <c>{hooks:[{command}]}</c> entries),
/// so the merge must preserve user-authored hooks AND every other settings key.
/// </summary>
public class GeminiHooksTests {
    const string KcapCommand = "kcap hook --gemini";

    static bool HasKcap(JsonArray entries) =>
        entries.Any(e => e?["hooks"] is JsonArray inner
                      && inner.Any(h => h?["command"]?.GetValue<string>() == KcapCommand));

    static bool HasCommand(JsonArray entries, string command) =>
        entries.Any(e => e?["hooks"] is JsonArray inner
                      && inner.Any(h => h?["command"]?.GetValue<string>() == command));

    [Test]
    public async Task fresh_install_merges_lifecycle_events() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        var ok = PluginCommand.InstallGeminiHooks(settingsPath);
        await Assert.That(ok).IsTrue();

        var hooks = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject()["hooks"]!.AsObject();

        foreach (var evt in new[] { "SessionStart", "SessionEnd", "Notification" }) {
            await Assert.That(HasKcap(hooks[evt]!.AsArray())).IsTrue();
        }
    }

    [Test]
    public async Task install_preserves_user_hooks_and_other_settings() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"security":{"auth":{"selectedType":"oauth-personal"}},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """);

        await Assert.That(PluginCommand.InstallGeminiHooks(settingsPath)).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        // Unrelated settings keys survive the merge.
        await Assert.That(root["security"]!["auth"]!["selectedType"]!.GetValue<string>()).IsEqualTo("oauth-personal");

        var start = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(HasCommand(start, "echo hi")).IsTrue();   // user hook preserved
        await Assert.That(HasKcap(start)).IsTrue();                 // kcap hook added
    }

    [Test]
    public async Task reinstall_does_not_duplicate_the_kcap_entry() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        PluginCommand.InstallGeminiHooks(settingsPath);
        PluginCommand.InstallGeminiHooks(settingsPath);

        var start = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!
            .AsObject()["hooks"]!["SessionStart"]!.AsArray();

        var kcapCount = start.Count(e => e?["hooks"] is JsonArray inner
                                      && inner.Any(h => h?["command"]?.GetValue<string>() == KcapCommand));
        await Assert.That(kcapCount).IsEqualTo(1);
    }

    [Test]
    public async Task remove_deletes_only_kcap_hooks() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"security":{"auth":{"selectedType":"oauth-personal"}},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """);

        PluginCommand.InstallGeminiHooks(settingsPath);
        await Assert.That(PluginCommand.RemoveGeminiHooks(settingsPath)).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        var start = root["hooks"]!["SessionStart"]!.AsArray();

        await Assert.That(HasKcap(start)).IsFalse();                                          // kcap gone
        await Assert.That(HasCommand(start, "echo hi")).IsTrue();                             // user hook kept
        await Assert.That(root["security"]!["auth"]!["selectedType"]!.GetValue<string>()).IsEqualTo("oauth-personal");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-gemini-hooks-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
