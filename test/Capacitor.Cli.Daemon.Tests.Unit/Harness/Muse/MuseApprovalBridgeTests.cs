using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseApprovalBridgeTests {
    static JsonElement ParamsOf(string line) {
        using var frame = MuseMspFrame.TryParse(line)!;

        return frame.Params!.Value.Clone();
    }

    static JsonElement WithSubagentOrigin(string line) {
        var node = JsonNode.Parse(line)!;
        node["params"]!["subagentOrigin"] = JsonNode.Parse("""{"childRunId":"r1","childSessionId":"s1","subagentId":"a1"}""");

        return JsonDocument.Parse(node.ToJsonString()).RootElement.GetProperty("params").Clone();
    }

    static AcpInteractionDecision Decision(string outcome, string? optionId = null, string? freeText = null) =>
        new(outcome, optionId, null, null, freeText, null);

    [Test]
    public async Task Tool_approval_maps_each_choice_to_an_option_with_its_kind() {
        var request = MuseApprovalBridge.ToInteraction("agent-1", "sess-1", ParamsOf(MuseMspWire.ApprovalRequested));

        await Assert.That(request.Kind).IsEqualTo("permission");
        await Assert.That(request.AgentId).IsEqualTo("agent-1");
        await Assert.That(request.AcpSessionId).IsEqualTo("sess-1");
        await Assert.That(request.ToolName).IsEqualTo("mcp__kcap_sessions__search_sessions");
        await Assert.That(request.ToolCallId).IsEqualTo("call_01a1369568de712aafb33a0f87cae86a");
        await Assert.That(request.Prompt).IsEqualTo("mcp__kcap_sessions__search_sessions");
        await Assert.That(request.IsMultiSelect).IsFalse();
        await Assert.That(request.Options!.Length).IsEqualTo(4);
        await Assert.That(request.Options![0]).IsEqualTo(new AcpInteractionOption("allow_once", "Allow once", null, "allow_once"));
        await Assert.That(request.Options![1].Kind).IsEqualTo("allow_always");
        await Assert.That(request.Options![2].OptionId).IsEqualTo("allow_local_mcp_tool");
        await Assert.That(request.Options![2].Kind).IsEqualTo("allow_always");
        await Assert.That(request.Options![3]).IsEqualTo(new AcpInteractionOption("abort", "Reject", null, "reject_once"));
    }

    [Test]
    public async Task Tool_input_carries_the_subject_and_the_parsed_raw_args() {
        var request = MuseApprovalBridge.ToInteraction("a", "s", ParamsOf(MuseMspWire.ApprovalRequested));
        var input   = request.ToolInput!.Value;

        await Assert.That(input.GetProperty("subject").GetProperty("kind").GetString()).IsEqualTo("tool");
        await Assert.That(input.GetProperty("args").GetProperty("query").GetString()).IsEqualTo("muse");
    }

    [Test]
    public async Task Unparseable_raw_args_are_carried_as_the_string() {
        var node = JsonNode.Parse(MuseMspWire.ApprovalRequested)!;
        node["params"]!["rawArgs"] = "not json {";
        var p = JsonDocument.Parse(node.ToJsonString()).RootElement.GetProperty("params").Clone();

        var request = MuseApprovalBridge.ToInteraction("a", "s", p);

        await Assert.That(request.ToolInput!.Value.GetProperty("args").GetString()).IsEqualTo("not json {");
    }

    [Test]
    public async Task Shell_approval_is_named_bash_and_prompts_with_the_command() {
        var request = MuseApprovalBridge.ToInteraction("a", "s", ParamsOf(MuseMspWire.ShellApprovalRequested));

        await Assert.That(request.ToolName).IsEqualTo("bash");
        await Assert.That(request.Prompt).IsEqualTo("echo hi > /workspace/out.txt");
        await Assert.That(request.Options!.Length).IsEqualTo(2);
        await Assert.That(request.Options![1].Kind).IsEqualTo("reject_once");
    }

    [Test]
    public async Task Subagent_origin_prefixes_the_prompt() {
        var tool  = MuseApprovalBridge.ToInteraction("a", "s", WithSubagentOrigin(MuseMspWire.ApprovalRequested));
        var shell = MuseApprovalBridge.ToInteraction("a", "s", WithSubagentOrigin(MuseMspWire.ShellApprovalRequested));

        await Assert.That(tool.Prompt).IsEqualTo("[subagent] mcp__kcap_sessions__search_sessions");
        await Assert.That(shell.Prompt).IsEqualTo("[subagent] echo hi > /workspace/out.txt");
    }

    [Test]
    public async Task Unknown_subject_kind_names_the_kind_and_an_unknown_decision_has_no_kind() {
        var p = JsonDocument.Parse(
            """{"toolCallId":"c","subject":{"kind":"path","path":"/x"},"availableChoices":[{"choiceId":"odd","label":"Odd","decision":"mystery"},{"choiceId":"abort","label":"Reject","decision":"abort"}]}""").RootElement;

        var request = MuseApprovalBridge.ToInteraction("a", "s", p);

        await Assert.That(request.ToolName).IsEqualTo("path");
        await Assert.That(request.Options![0].Kind).IsNull();
    }

    [Test]
    public async Task Selecting_an_offered_choice_returns_that_choice() {
        var decision = MuseApprovalBridge.ToDecision(
            Decision("allow_always", "allow_session"), ParamsOf(MuseMspWire.ApprovalRequested));

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("allow_session", null));
    }

    [Test]
    public async Task Reject_carries_free_text_only_because_that_choice_accepts_feedback() {
        var p = ParamsOf(MuseMspWire.ApprovalRequested);

        var reject = MuseApprovalBridge.ToDecision(Decision("deny", "abort", "too risky"), p);
        var allow  = MuseApprovalBridge.ToDecision(Decision("allow_once", "allow_once", "ignored"), p);

        await Assert.That(reject).IsEqualTo(new MuseApprovalDecision("abort", "too risky"));
        await Assert.That(allow).IsEqualTo(new MuseApprovalDecision("allow_once", null));
    }

    [Test]
    [Arguments("cancel", null)]
    [Arguments("timeout", null)]
    [Arguments("cancel", "allow_once")]
    [Arguments("timeout", "allow_session")]
    [Arguments("allow_once", "no_such_choice")]
    [Arguments("allow_once", null)]
    [Arguments("nonsense", "allow_once")]
    [Arguments("deny", "allow_once")]
    public async Task Anything_but_a_clean_selection_aborts(string outcome, string? optionId) {
        var decision = MuseApprovalBridge.ToDecision(Decision(outcome, optionId), ParamsOf(MuseMspWire.ApprovalRequested));

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", "kcap: declined"));
    }

    [Test]
    [Arguments("cancel", null)]
    [Arguments("allow_once", "no_such_choice")]
    [Arguments("timeout", "a")]
    public async Task Without_an_abort_choice_the_fallback_is_the_literal_abort_never_an_approving_choice(string outcome, string? optionId) {
        var p = JsonDocument.Parse(
            """{"availableChoices":[{"choiceId":"a","label":"A","decision":"approved"},{"choiceId":"b","label":"B","decision":"approvedForSession"}]}""").RootElement;

        var decision = MuseApprovalBridge.ToDecision(Decision(outcome, optionId), p);

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", "kcap: declined"));
    }
}
