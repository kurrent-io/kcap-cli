using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Claude saves an "Allow always" only from a decision's <c>updatedPermissions</c>, while the card, the server and the
/// stored response carry it as one <c>toolAlwaysAllow</c> entry, which older servers and clients still send. It reaches
/// Claude as the rules Claude itself suggested for the call, or as a session rule for the whole tool.
/// </summary>
public class ClaudePermissionsTests {
    const string Suggestions = """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo third:*"}],"behavior":"allow","destination":"localSettings"}]""";

    static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Always_allow_is_the_web_ui_shape() {
        var el = ClaudePermissions.AlwaysAllow("Bash");
        await Assert.That(el.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }

    [Test]
    public async Task Always_allow_saves_the_rules_claude_suggested() {
        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element(Suggestions));

        await Assert.That(updates?.ToJsonString()).IsEqualTo(Suggestions);
    }

    [Test]
    public async Task Always_allow_with_nothing_suggested_saves_a_session_rule_for_the_tool() {
        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element("[]"));

        await Assert.That(updates?.ToJsonString())
            .IsEqualTo("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"}]""");
    }

    /// <summary>An ACP answer's <c>true</c>, or nothing at all, asks Claude to save nothing.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("true")]
    [Arguments("""{"allow":["Bash(ls:*)"]}""")]
    [Arguments("""[{"type":"toolAlwaysAllow"}]""")]
    public async Task Anything_but_always_allow_saves_nothing(string? applyPermissions) {
        var updates = ClaudePermissions.UpdatesFor(applyPermissions is null ? null : Element(applyPermissions), Element(Suggestions));

        await Assert.That(updates).IsNull();
    }
}
