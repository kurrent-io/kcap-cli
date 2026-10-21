using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

/// <summary>
/// A hosted Grok's <c>_x.ai/ask_user_question</c> reaches the dashboard as one question card per
/// question, and the answers go back in the reply shape Grok 1.0.62 accepts
/// (<c>docs/probes/2026-10-21-grok-ask-user-question/</c>).
/// </summary>
public class GrokQuestionsTests {
    const string SessionId = "01a15c7b-3f8d-7012-ab33-842ccc5a834d";

    static AcpRequest Ask(string questions) => new(7, "_x.ai/ask_user_question", JsonDocument.Parse($$"""
        {"sessionId":"{{SessionId}}","toolCallId":"call-1","mode":"default","questions":{{questions}}}
        """).RootElement.Clone());

    const string Colour = """[{"question":"Which colour?","options":[{"label":"Red","description":"Warm"},{"label":"Blue","description":"Cool"}],"multiSelect":null}]""";

    static (AcpInteractionBridge Bridge, List<AcpInteractionRequest> Asked) Bridge(
            Func<AcpInteractionRequest, AcpInteractionDecision> answer,
            AcpUnattendedInteractionPolicy policy = AcpUnattendedInteractionPolicy.Disabled) {
        var asked = new List<AcpInteractionRequest>();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, _) => { asked.Add(req); return Task.FromResult(answer(req)); },
            agentId: "agent-1", logger: NullLogger.Instance, time: TimeProvider.System,
            unattendedPolicy: policy,
            questionProtocol: GrokQuestions.Protocol);

        return (bridge, asked);
    }

    static AcpInteractionDecision Picked(params string[] ids) =>
        new("answered", ids[0], null, null, null, null, SelectedOptionIds: ids);

    static string Json(JsonElement? result) => result!.Value.GetRawText();

    [Test]
    public async Task A_question_is_asked_as_a_question_card_and_its_answer_goes_back_to_grok() {
        var (bridge, asked) = Bridge(req => Picked(req.Options![1].OptionId));

        var result = await bridge.HandleAsync(Ask(Colour), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"accepted","answers":{"Which colour?":"Blue"}}""");
        var card = asked.Single();
        await Assert.That(card.Kind).IsEqualTo("elicitation");
        await Assert.That(card.AcpSessionId).IsEqualTo(SessionId);
        await Assert.That(card.Prompt).IsEqualTo("Which colour?");
        await Assert.That(card.Options!.Select(o => $"{o.Label}: {o.Description}")).IsEquivalentTo(["Red: Warm", "Blue: Cool"]);
        await Assert.That(card.IsMultiSelect).IsFalse();
    }

    [Test]
    public async Task A_multi_select_question_answers_with_every_chosen_label() {
        var (bridge, asked) = Bridge(req => Picked(req.Options![0].OptionId, req.Options![1].OptionId));

        var result = await bridge.HandleAsync(Ask(Colour.Replace("\"multiSelect\":null", "\"multiSelect\":true")), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"accepted","answers":{"Which colour?":["Red","Blue"]}}""");
        await Assert.That(asked.Single().IsMultiSelect).IsTrue();
    }

    [Test]
    public async Task Several_questions_are_asked_in_turn_and_answered_together() {
        var (bridge, asked) = Bridge(req => Picked(req.Options![0].OptionId));

        var result = await bridge.HandleAsync(Ask("""
            [{"question":"Which colour?","options":[{"label":"Red"},{"label":"Blue"}]},
             {"question":"Which size?","options":[{"label":"Small"},{"label":"Large"}]}]
            """), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"accepted","answers":{"Which colour?":"Red","Which size?":"Small"}}""");
        await Assert.That(asked.Select(a => a.Prompt ?? "")).IsEquivalentTo(["(1 of 2) Which colour?", "(2 of 2) Which size?"]);
    }

    [Test]
    public async Task A_question_without_options_takes_a_typed_answer() {
        var (bridge, _) = Bridge(_ => new AcpInteractionDecision("answered", null, null, null, "Teal", null));

        var result = await bridge.HandleAsync(Ask("""[{"question":"Which colour?","options":[]}]"""), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"accepted","answers":{"Which colour?":"Teal"}}""");
    }

    /// <summary>A declined card ends the request: the questions after it are not asked.</summary>
    [Test]
    public async Task A_declined_question_cancels_the_whole_request() {
        var (bridge, asked) = Bridge(_ => new AcpInteractionDecision("cancel", null, null, null, null, null));

        var result = await bridge.HandleAsync(Ask("""
            [{"question":"Which colour?","options":[{"label":"Red"}]},{"question":"Which size?","options":[{"label":"Small"}]}]
            """), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"cancelled"}""");
        await Assert.That(asked.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_request_with_no_questions_is_cancelled_without_asking() {
        var (bridge, asked) = Bridge(_ => throw new InvalidOperationException("nothing should be asked"));

        var result = await bridge.HandleAsync(Ask("[]"), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"cancelled"}""");
        await Assert.That(asked).IsEmpty();
    }

    /// <summary>An unattended reviewer has nobody to ask, so it proceeds on its own judgement.</summary>
    [Test]
    public async Task An_unattended_launch_declines_without_asking() {
        var (bridge, asked) = Bridge(_ => throw new InvalidOperationException("nothing should be asked"), AcpUnattendedInteractionPolicy.AutoApprove);

        var result = await bridge.HandleAsync(Ask(Colour), CancellationToken.None);

        await Assert.That(Json(result)).IsEqualTo("""{"outcome":"cancelled"}""");
        await Assert.That(asked).IsEmpty();
    }

    [Test]
    public async Task A_vendor_without_a_question_method_leaves_it_unhandled() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (_, _) => throw new InvalidOperationException("nothing should be asked"),
            agentId: "agent-1", logger: NullLogger.Instance, time: TimeProvider.System);

        await Assert.That(await bridge.HandleAsync(Ask(Colour), CancellationToken.None)).IsNull();
    }
}
