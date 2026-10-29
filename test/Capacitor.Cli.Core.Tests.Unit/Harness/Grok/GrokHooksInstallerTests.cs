using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokHooksInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string HooksPath => Tmp.PathTo(".grok", "hooks", "kcap.json");

    /// <summary>Grok reads only <c>type</c>, <c>command</c>, <c>url</c>, <c>timeout</c> and
    /// <c>env</c> on a handler and refuses a file carrying Claude-only fields, so nothing else is written.</summary>
    [Test]
    public async Task Install_creates_the_hooks_directory_and_writes_only_grok_s_handler_fields() {
        await Assert.That(GrokHooksInstaller.Install(HooksPath)).IsTrue();

        var hooks = (JsonNode.Parse(File.ReadAllText(HooksPath))!["hooks"] as JsonObject)!;
        await Assert.That(hooks.Select(h => h.Key)).IsEquivalentTo(GrokHooksInstaller.Events);

        foreach (var (_, groups) in hooks) {
            var handler = (groups!.AsArray().Single()!["hooks"]!.AsArray().Single() as JsonObject)!;
            await Assert.That(handler.Select(f => f.Key)).IsEquivalentTo(new[] { "type", "command", "timeout" });
            await Assert.That(handler["command"]!.GetValue<string>()).IsEqualTo(GrokHooksInstaller.HookCommand);
        }

        await Assert.That(GrokHooksInstaller.IsInstalled(HooksPath)).IsTrue();
    }

    [Test]
    public async Task Install_is_idempotent() {
        GrokHooksInstaller.Install(HooksPath);
        var first = File.ReadAllText(HooksPath);

        GrokHooksInstaller.Install(HooksPath);

        await Assert.That(File.ReadAllText(HooksPath)).IsEqualTo(first);
    }

    [Test]
    public async Task Remove_deletes_only_kcap_s_file_and_marker() {
        GrokHooksInstaller.Install(HooksPath);
        var userHook = Tmp.CreateFile(Path.Combine(".grok", "hooks", "mine.json"), """{"hooks":{}}""");

        await Assert.That(GrokHooksInstaller.Remove(HooksPath)).IsTrue();

        await Assert.That(File.Exists(HooksPath)).IsFalse();
        await Assert.That(File.Exists(userHook)).IsTrue();
        await Assert.That(GrokHooksInstaller.IsInstalled(HooksPath)).IsFalse();
    }

    [Test]
    public async Task Not_installed_when_nothing_was_written() {
        await Assert.That(GrokHooksInstaller.IsInstalled(HooksPath)).IsFalse();
    }

    [Test]
    [Arguments("[]")]
    [Arguments("{\"hooks\":[]}")]
    [Arguments("not json")]
    public async Task A_file_of_the_wrong_shape_is_not_installed(string content) {
        Tmp.CreateFile(Path.Combine(".grok", "hooks", "kcap.json"), content);

        await Assert.That(GrokHooksInstaller.IsInstalled(HooksPath)).IsFalse();
    }
}
