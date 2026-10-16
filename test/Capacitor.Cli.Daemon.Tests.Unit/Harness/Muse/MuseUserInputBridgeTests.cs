using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseUserInputBridgeTests {
    static JsonElement ParamsOf(string line) {
        using var frame = MuseMspFrame.TryParse(line)!;

        return frame.Params!.Value.Clone();
    }

    [Test]
    public async Task Question_maps_to_an_elicitation_with_the_labels_as_options() {
        var request = MuseUserInputBridge.ToInteraction("agent-1", "sess-1", ParamsOf(MuseMspWire.UserInputRequested));

        await Assert.That(request.Kind).IsEqualTo("elicitation");
        await Assert.That(request.AgentId).IsEqualTo("agent-1");
        await Assert.That(request.AcpSessionId).IsEqualTo("sess-1");
        await Assert.That(request.ToolName).IsEqualTo("request_user_input");
        await Assert.That(request.ToolCallId).IsEqualTo("call_01a136969dc1778780e4116f3c3c9e6c");
        await Assert.That(request.Prompt).IsEqualTo("Colour: Which colour do you prefer?");
        await Assert.That(request.IsMultiSelect).IsFalse();
        await Assert.That(request.Options!.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "Red", "Blue" });
        await Assert.That(request.Options![1].Label).IsEqualTo("Blue");
    }

    [Test]
    public async Task Multiple_selection_mode_is_multi_select() {
        var p = JsonDocument.Parse(
            """{"toolName":"t","toolCallId":"c","questions":[{"id":"q","header":"H","question":"Q?","selection":{"mode":"multiple"},"options":[{"label":"A"},{"label":"B"}]}]}""").RootElement;

        await Assert.That(MuseUserInputBridge.ToInteraction("a", "s", p).IsMultiSelect).IsTrue();
    }

    static JsonElement MultiParams() =>
        JsonDocument.Parse(
            """{"toolName":"t","toolCallId":"c","questions":[{"id":"q","header":"H","question":"Q?","selection":{"mode":"multiple"},"options":[{"label":"A"},{"label":"B"}]}]}""").RootElement;

    [Test]
    [Arguments("Blue", null)]
    [Arguments(null, "Blue")]
    public async Task A_selected_option_answers_the_first_question_as_a_single_label(string? label, string? optionId) {
        var decision = new AcpInteractionDecision("answered", optionId, label, null, null, null);

        var answers = MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested));

        await Assert.That(answers!.Count).IsEqualTo(1);
        await Assert.That(answers[0]).IsEqualTo(new MuseUserInputAnswer("colour_pref", SelectedLabel: "Blue"));
    }

    [Test]
    public async Task Multiple_mode_sends_every_selected_label() {
        var decision = new AcpInteractionDecision("answered", "A", "A", null, null, null, ["A", "B"], ["A", "B"]);

        var answers = MuseUserInputBridge.ToAnswers(decision, MultiParams());

        await Assert.That(answers![0].SelectedLabel).IsNull();
        await Assert.That(answers[0].SelectedLabels!.ToArray()).IsEquivalentTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Multiple_mode_with_only_the_scalar_selection_sends_a_one_element_list() {
        var decision = new AcpInteractionDecision("answered", null, "B", null, null, null);

        var answers = MuseUserInputBridge.ToAnswers(decision, MultiParams());

        await Assert.That(answers![0].SelectedLabels!.ToArray()).IsEquivalentTo(new[] { "B" });
    }

    [Test]
    public async Task Free_text_answers_when_no_option_was_selected() {
        var decision = new AcpInteractionDecision("answered", null, null, null, "Green", null);

        var answers = MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested));

        await Assert.That(answers![0]).IsEqualTo(new MuseUserInputAnswer("colour_pref", FreeText: "Green"));
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("timeout")]
    [Arguments("deny")]
    public async Task A_dismissed_question_yields_no_answers_even_with_a_selection(string outcome) {
        var decision = new AcpInteractionDecision(outcome, "Blue", "Blue", null, "x", null);

        await Assert.That(MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested))).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task An_answered_outcome_with_nothing_usable_yields_no_answers(string? freeText) {
        var decision = new AcpInteractionDecision("answered", null, null, null, freeText, null);

        await Assert.That(MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested))).IsNull();
    }
}
