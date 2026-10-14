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

    [Test]
    [Arguments("Blue", null)]
    [Arguments(null, "Blue")]
    public async Task A_selected_option_answers_the_first_question(string? label, string? optionId) {
        var decision = new AcpInteractionDecision("answered", optionId, label, null, null, null);

        var answers = MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested));

        await Assert.That(answers!.Count).IsEqualTo(1);
        await Assert.That(answers[0].QuestionId).IsEqualTo("colour_pref");
        await Assert.That(answers[0].SelectedLabel).IsEqualTo("Blue");
    }

    [Test]
    public async Task Free_text_answers_when_no_option_was_selected() {
        var decision = new AcpInteractionDecision("answered", null, null, null, "Green", null);

        var answers = MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested));

        await Assert.That(answers![0].SelectedLabel).IsEqualTo("Green");
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("timeout")]
    [Arguments("deny")]
    public async Task A_dismissed_question_yields_no_answers(string outcome) {
        var decision = new AcpInteractionDecision(outcome, null, null, null, null, null);

        await Assert.That(MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested))).IsNull();
    }

    [Test]
    public async Task An_answered_outcome_with_nothing_chosen_yields_no_answers() {
        var decision = new AcpInteractionDecision("answered", null, null, null, null, null);

        await Assert.That(MuseUserInputBridge.ToAnswers(decision, ParamsOf(MuseMspWire.UserInputRequested))).IsNull();
    }
}
