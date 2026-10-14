using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseUnattendedApprovalsTests {
    const string Unadmitted = "unattended reviewer: not admitted";

    static JsonElement ParamsOf(string line) {
        using var frame = MuseMspFrame.TryParse(line)!;

        return frame.Params!.Value.Clone();
    }

    static AcpMcpServerSpec Server(string name) => new(name, "kcap", null, null);

    static IReadOnlySet<string> Admitted(params string[] servers) =>
        MuseUnattendedApprovals.AdmittedFor([.. servers.Select(Server)]);

    static JsonElement ToolApproval(string toolName) =>
        JsonDocument.Parse(
            $$"""{"subject":{"kind":"tool","toolName":"{{toolName}}"},"availableChoices":[{"choiceId":"allow_once","label":"Allow once","decision":"approved"},{"choiceId":"allow_session","label":"Allow for this session","decision":"approvedForSession"},{"choiceId":"abort","label":"Reject","decision":"abort","acceptsFeedback":true}]}""")
            .RootElement;

    [Test]
    public async Task Result_channel_and_read_only_server_are_admitted_under_the_names_muse_shows() {
        var admitted = Admitted(KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions");

        await Assert.That(admitted.Contains("mcp__kcap_flow_result__submit_review_result")).IsTrue();
        await Assert.That(admitted.Contains("mcp__kcap_flow_result__send_flow_message")).IsTrue();
        await Assert.That(admitted.Contains("mcp__kcap_sessions__search_sessions")).IsTrue();
        await Assert.That(admitted.Contains("mcp__kcap_review__get_pr_summary")).IsFalse();
        await Assert.That(admitted.Contains("submit_review_result")).IsFalse();
    }

    [Test]
    public async Task The_result_channel_name_is_matched_case_insensitively() {
        var admitted = Admitted("KCAP-Flow-Result");

        await Assert.That(admitted.Contains("mcp__KCAP_Flow_Result__submit_review_result")).IsTrue();
    }

    [Test]
    public async Task No_servers_admit_nothing() {
        await Assert.That(MuseUnattendedApprovals.AdmittedFor([])).IsSameReferenceAs(FrozenSet<string>.Empty);
    }

    [Test]
    public async Task An_unclassified_server_fails_the_launch() {
        var ex = Assert.Throws<InvalidOperationException>(() => Admitted("kcap-memory"));

        await Assert.That(ex!.Message).Contains("muse_reviewer_unclassified_server");
    }

    [Test]
    public async Task An_admitted_tool_gets_the_allow_once_choice() {
        var admitted = Admitted(KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions");

        var decision = MuseUnattendedApprovals.Decide(ParamsOf(MuseMspWire.ApprovalRequested), admitted);

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("allow_once", null));
    }

    [Test]
    public async Task A_shell_approval_is_refused() {
        var admitted = Admitted(KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions");

        var decision = MuseUnattendedApprovals.Decide(ParamsOf(MuseMspWire.ShellApprovalRequested), admitted);

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", Unadmitted));
    }

    [Test]
    [Arguments("mcp__kcap_memory__save_memory")]
    [Arguments("mcp__kcap_sessions__not_a_tool")]
    [Arguments("Running: mcp__kcap_sessions__search_sessions")]
    [Arguments("mcp__kcap_sessions__search_sessions ")]
    [Arguments("MCP__KCAP_SESSIONS__SEARCH_SESSIONS")]
    [Arguments("")]
    public async Task A_tool_outside_the_admitted_set_is_refused(string toolName) {
        var decision = MuseUnattendedApprovals.Decide(ToolApproval(toolName), Admitted("kcap-sessions"));

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", Unadmitted));
    }

    [Test]
    public async Task An_empty_admitted_set_refuses_every_tool() {
        var decision = MuseUnattendedApprovals.Decide(ParamsOf(MuseMspWire.ApprovalRequested), FrozenSet<string>.Empty);

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", Unadmitted));
    }

    [Test]
    public async Task An_admitted_tool_without_an_allow_once_choice_is_refused() {
        var p = JsonDocument.Parse(
            """{"subject":{"kind":"tool","toolName":"mcp__kcap_sessions__search_sessions"},"availableChoices":[{"choiceId":"abort","label":"Reject","decision":"abort"}]}""").RootElement;

        var decision = MuseUnattendedApprovals.Decide(p, Admitted("kcap-sessions"));

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", Unadmitted));
    }

    [Test]
    public async Task A_payload_without_a_subject_is_refused_and_never_falls_back_to_an_allow_choice() {
        var p = JsonDocument.Parse("""{"availableChoices":[{"choiceId":"allow_once","label":"x","decision":"approved"}]}""").RootElement;

        var decision = MuseUnattendedApprovals.Decide(p, Admitted("kcap-sessions"));

        await Assert.That(decision).IsEqualTo(new MuseApprovalDecision("abort", Unadmitted));
    }
}
