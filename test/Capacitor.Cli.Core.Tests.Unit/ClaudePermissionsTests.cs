using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Claude saves an "Allow always" only from a decision's <c>updatedPermissions</c>, while the card, the server and the
/// stored response carry it as one <c>toolAlwaysAllow</c> entry, which older servers and clients still send. It reaches
/// Claude as the rules Claude itself suggested for the approved tool, or as a session rule for that tool. The approved
/// tool is the call's own.
/// </summary>
public class ClaudePermissionsTests {
    const string BashSuggestion = """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo third:*"}],"behavior":"allow","destination":"localSettings"}""";

    static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Always_allow_is_the_web_ui_shape() {
        var el = ClaudePermissions.AlwaysAllow("Bash");
        await Assert.That(el.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }

    [Test]
    public async Task Always_allow_saves_the_rules_claude_suggested() {
        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element($"[{BashSuggestion}]"), "Bash");

        await Assert.That(updates?.ToJsonString()).IsEqualTo($"[{BashSuggestion}]");
    }

    /// <summary>The approval covers the call's tool and nothing else Claude suggested alongside it.</summary>
    [Test]
    public async Task Always_allow_keeps_only_the_approved_tools_suggestions() {
        var mixed = """
            [{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo third:*"},{"toolName":"Read","ruleContent":"//etc/**"}],"behavior":"allow","destination":"localSettings"},
             {"type":"addRules","rules":[{"toolName":"WebFetch","ruleContent":"domain:example.com"}],"behavior":"allow","destination":"localSettings"},
             {"type":"setMode","mode":"acceptEdits","destination":"session"},
             {"type":"addDirectories","directories":["/etc"],"destination":"session"}]
            """;

        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element(mixed), "Bash");

        await Assert.That(updates?.ToJsonString()).IsEqualTo($"[{BashSuggestion}]");
    }

    /// <summary>Allow always adds allow rules and nothing else, so a suggestion that would remove, replace, deny or ask
    /// is passed over, and the tool gets its session rule instead.</summary>
    [Test]
    [Arguments("removeRules", "allow")]
    [Arguments("replaceRules", "allow")]
    [Arguments("addRules", "deny")]
    [Arguments("addRules", "ask")]
    public async Task A_suggestion_that_is_not_an_allow_rule_is_not_saved(string type, string behavior) {
        var suggestion = $$"""[{"type":"{{type}}","rules":[{"toolName":"Bash","ruleContent":"echo third:*"}],"behavior":"{{behavior}}","destination":"localSettings"}]""";

        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element(suggestion), "Bash");

        await Assert.That(updates?.ToJsonString())
            .IsEqualTo("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"}]""");
    }

    [Test]
    public async Task Always_allow_with_nothing_suggested_saves_a_session_rule_for_the_tool() {
        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element("[]"), "Bash");

        await Assert.That(updates?.ToJsonString())
            .IsEqualTo("""[{"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow","destination":"session"}]""");
    }

    /// <summary>An answer naming a tool other than the call's approves nothing to save.</summary>
    [Test]
    public async Task Always_allow_for_a_tool_other_than_the_calls_saves_nothing() {
        var updates = ClaudePermissions.UpdatesFor(ClaudePermissions.AlwaysAllow("Bash"), Element($"[{BashSuggestion}]"), "Read");

        await Assert.That(updates).IsNull();
    }

    /// <summary>An ACP answer's <c>true</c>, or nothing at all, asks Claude to save nothing.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("true")]
    [Arguments("""{"allow":["Bash(ls:*)"]}""")]
    [Arguments("""[{"type":"toolAlwaysAllow"}]""")]
    public async Task Anything_but_always_allow_saves_nothing(string? applyPermissions) {
        var updates = ClaudePermissions.UpdatesFor(applyPermissions is null ? null : Element(applyPermissions), Element($"[{BashSuggestion}]"), "Bash");

        await Assert.That(updates).IsNull();
    }
}
