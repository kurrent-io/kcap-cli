using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseHooksParserTests {
    [Test]
    public async Task Group_has_no_matcher_and_the_session_end_budget_is_longer() {
        var start = MuseHooksParser.BuildKcapGroup("SessionStart");
        var end   = MuseHooksParser.BuildKcapGroup("SessionEnd");
        await Assert.That(start["matcher"]).IsNull();
        await Assert.That(start["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kcap hook --muse --no-update-check");
        await Assert.That(start["hooks"]![0]!["timeout"]!.GetValue<int>()).IsEqualTo(5);
        await Assert.That(end["hooks"]![0]!["timeout"]!.GetValue<int>()).IsEqualTo(15);
    }

    [Test]
    [Arguments("kcap hook --muse --no-update-check", true)]
    [Arguments("/usr/local/bin/kcap hook --muse --no-update-check", true)]
    [Arguments("kcap hook --muse", true)]
    [Arguments("kcap hook --gemini", false)]
    [Arguments("echo kcap hook --muse", false)]
    public async Task Command_recognition_is_by_executable_and_exact_args(string command, bool expected) =>
        await Assert.That(MuseHooksParser.IsCapacitorMuseHookCommand(command)).IsEqualTo(expected);

    [Test]
    public async Task Installed_events_reads_only_kcap_groups() {
        var root = (JsonObject)JsonNode.Parse("""{"schema_version":1,"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --muse --no-update-check","timeout":5}]}],"Stop":[{"hooks":[{"type":"command","command":"say hi","timeout":5}]}]}}""")!;
        await Assert.That(MuseHooksParser.InstalledEvents(root)).IsEquivalentTo(new[] { "SessionStart" });
        await Assert.That(MuseHooksParser.HasCapacitorHooksFor(root, MuseHooksParser.MuseHookEvents)).IsFalse();
    }

    [Test]
    [Arguments("""{"hooks":[{"type":"command","command":42}]}""")]
    [Arguments("""{"hooks":[{"type":"command","command":["kcap","hook","--muse"]}]}""")]
    [Arguments("""{"command":42}""")]
    [Arguments("""{"command":["kcap","hook","--muse"]}""")]
    public async Task A_non_string_command_is_not_ours(string group) =>
        await Assert.That(MuseHooksParser.EntryReferencesCapacitorMuseHook(JsonNode.Parse(group))).IsFalse();
}
