using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiDialogBridgeTests {
    const string Select  = """{"type":"extension_ui_request","id":"ui-1","method":"select","title":"Pick one","options":["Allow","Block","Allow"]}""";
    const string Confirm = """{"type":"extension_ui_request","id":"ui-1","method":"confirm","title":"Clear session?","message":"All messages will be lost."}""";
    const string Input   = """{"type":"extension_ui_request","id":"ui-1","method":"input","title":"Your name","placeholder":"…"}""";
    const string Editor  = """{"type":"extension_ui_request","id":"ui-1","method":"editor","title":"Edit the plan","prefill":"step 1"}""";

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static AcpInteractionDecision Answered(string? optionId = null, string? freeText = null) =>
        new("answered", optionId, null, null, freeText, null);

    static AcpInteractionRequest Ask(string json) => PiDialogBridge.ToInteraction("agent-1", "sess-1", Parse(json))!.Value;

    [Test]
    public async Task A_select_asks_its_options_keyed_by_position_so_repeated_labels_stay_distinct() {
        var request = Ask(Select);

        await Assert.That(request.Kind).IsEqualTo("elicitation");
        await Assert.That(request.AgentId).IsEqualTo("agent-1");
        await Assert.That(request.AcpSessionId).IsEqualTo("sess-1");
        await Assert.That(request.Prompt).IsEqualTo("Pick one");
        await Assert.That(request.IsMultiSelect).IsFalse();
        await Assert.That(request.Options!.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "0", "1", "2" });
        await Assert.That(request.Options!.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "Allow", "Block", "Allow" });
    }

    [Test]
    public async Task A_confirm_asks_yes_or_no_under_its_title_and_message() {
        var request = Ask(Confirm);

        await Assert.That(request.Prompt).IsEqualTo("Clear session?\n\nAll messages will be lost.");
        await Assert.That(request.Options!.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "Yes", "No" });
    }

    /// <summary>No options is what both viewer surfaces render as a free-text question.</summary>
    [Test]
    public async Task Input_and_editor_ask_for_free_text() {
        var input  = Ask(Input);
        var editor = Ask(Editor);

        await Assert.That(input.Options).IsNull();
        await Assert.That(input.Prompt).IsEqualTo("Your name");
        await Assert.That(editor.Options).IsNull();
        await Assert.That(editor.Prompt).IsEqualTo("Edit the plan\n\nCurrent text:\nstep 1");
    }

    [Test]
    public async Task An_oversized_prompt_is_capped() {
        var json = $$"""{"id":"ui-1","method":"editor","title":"t","prefill":"{{new string('x', PiDialogBridge.MaxPromptLength * 2)}}"}""";

        await Assert.That(Ask(json).Prompt!.Length).IsEqualTo(PiDialogBridge.MaxPromptLength);
    }

    [Test]
    public async Task A_select_with_more_options_than_the_server_takes_is_not_routed() {
        var options = string.Join(",", Enumerable.Range(0, PiDialogBridge.MaxOptions + 1).Select(i => $"\"o{i}\""));

        await Assert.That(PiDialogBridge.ToInteraction("a", "s", Parse($$"""{"id":"ui-1","method":"select","title":"t","options":[{{options}}]}"""))).IsNull();
    }

    [Test]
    [Arguments("""{"id":"ui-1","method":"select","title":"t","options":[]}""")]
    [Arguments("""{"id":"ui-1","method":"select","title":"t"}""")]
    [Arguments("""{"id":"ui-1","method":"notify","message":"m"}""")]
    public async Task A_request_with_nothing_to_ask_is_not_routed(string json) =>
        await Assert.That(PiDialogBridge.ToInteraction("a", "s", Parse(json))).IsNull();

    [Test]
    [Arguments(Select, "1", null, """{"type":"extension_ui_response","id":"ui-1","value":"Block"}""")]
    [Arguments(Select, null, "Something else", """{"type":"extension_ui_response","id":"ui-1","value":"Something else"}""")]
    [Arguments(Select, "9", null, """{"type":"extension_ui_response","id":"ui-1","cancelled":true}""")]
    [Arguments(Confirm, "yes", null, """{"type":"extension_ui_response","id":"ui-1","confirmed":true}""")]
    [Arguments(Confirm, "no", null, """{"type":"extension_ui_response","id":"ui-1","confirmed":false}""")]
    [Arguments(Confirm, null, "maybe", """{"type":"extension_ui_response","id":"ui-1","cancelled":true}""")]
    [Arguments(Input, null, "Ada", """{"type":"extension_ui_response","id":"ui-1","value":"Ada"}""")]
    [Arguments(Input, null, " ", """{"type":"extension_ui_response","id":"ui-1","cancelled":true}""")]
    [Arguments(Editor, null, "step 2", """{"type":"extension_ui_response","id":"ui-1","value":"step 2"}""")]
    public async Task An_answer_maps_to_the_response_pi_expects(string request, string? optionId, string? freeText, string expected) =>
        await Assert.That(PiDialogBridge.ToResponse("ui-1", Answered(optionId, freeText), Parse(request))).IsEqualTo(expected);

    [Test]
    [Arguments("cancel")]
    [Arguments("timeout")]
    [Arguments("deny")]
    public async Task Anything_but_an_answer_cancels(string outcome) {
        var decision = new AcpInteractionDecision(outcome, "yes", "Yes", null, null, null);

        await Assert.That(PiDialogBridge.ToResponse("ui-1", decision, Parse(Confirm)))
            .IsEqualTo("""{"type":"extension_ui_response","id":"ui-1","cancelled":true}""");
    }
}
