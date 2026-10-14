using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseUnattendedApprovalsTests {
    const string Unadmitted = "unattended reviewer: not admitted";
    const string SearchTool = "mcp__kcap_sessions__search_sessions";

    static JsonElement ParamsOf(string line) {
        using var frame = MuseMspFrame.TryParse(line)!;

        return frame.Params!.Value.Clone();
    }

    static readonly LaunchIdentity Canonical = LaunchIdentity.FromGuids(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"), aliasResultChannel: false);

    static readonly LaunchIdentity Aliasing = LaunchIdentity.FromGuids(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"), aliasResultChannel: true);

    const string ChannelAlias = "kcap_flow_result_11111111111141118111111111111111";
    const string ServerSuffix = "_33333333333343338333333333333333";

    static AcpMcpServerSpec Server(string name) => new(name, "kcap", null, null);

    /// <summary>The launch's servers under the names <paramref name="identity"/> gives them on the wire.</summary>
    static IReadOnlyList<AcpMcpServerSpec> Servers(LaunchIdentity identity, params string[] canonicalIds) =>
        [.. canonicalIds.Select(id => Server(KcapMcpRegistry.IsReservedResultChannel(id)
            ? identity.ResultChannelWireName
            : identity.AllowlistWireName(id)))];

    static IReadOnlySet<string> Admitted(params string[] servers) =>
        MuseUnattendedApprovals.AdmittedFor(Servers(Canonical, servers), Canonical);

    static JsonElement ToolApproval(string toolName) =>
        JsonDocument.Parse(
            $$"""{"subject":{"kind":"tool","toolName":"{{toolName}}"},"availableChoices":[{"choiceId":"allow_once","label":"Allow once","decision":"approved"},{"choiceId":"allow_session","label":"Allow for this session","decision":"approvedForSession"},{"choiceId":"abort","label":"Reject","decision":"abort","acceptsFeedback":true}]}""")
            .RootElement;

    [Test]
    public async Task Each_server_is_admitted_under_its_per_launch_alias() {
        var admitted = MuseUnattendedApprovals.AdmittedFor(
            Servers(Aliasing, KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions"), Aliasing);

        await Assert.That(admitted.Contains($"mcp__{ChannelAlias}__submit_review_result")).IsTrue();
        await Assert.That(admitted.Contains($"mcp__{ChannelAlias}__send_flow_message")).IsTrue();
        await Assert.That(admitted.Contains($"mcp__kcap_sessions{ServerSuffix}__search_sessions")).IsTrue();
    }

    /// <summary>A workspace-declared server can take a kcap server's canonical name, so the canonical
    /// names must never be admitted for an aliasing launch.</summary>
    [Test]
    public async Task The_canonical_names_are_not_admitted_for_an_aliasing_launch() {
        var admitted = MuseUnattendedApprovals.AdmittedFor(
            Servers(Aliasing, KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions"), Aliasing);

        await Assert.That(admitted.Contains("mcp__kcap_flow_result__submit_review_result")).IsFalse();
        await Assert.That(admitted.Contains(SearchTool)).IsFalse();
        await Assert.That(admitted.Contains("submit_review_result")).IsFalse();
    }

    [Test]
    public async Task A_server_name_the_launch_identity_did_not_produce_fails_the_launch() {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MuseUnattendedApprovals.AdmittedFor([Server("kcap-sessions")], Aliasing));

        await Assert.That(ex!.Message).Contains("muse_reviewer_unclassified_server");
    }

    [Test]
    public async Task The_review_context_server_admits_its_one_tool() {
        var admitted = MuseUnattendedApprovals.AdmittedFor(Servers(Aliasing, "kcap-review-context"), Aliasing);

        await Assert.That(admitted).IsEquivalentTo(new[] { $"mcp__kcap_review_context{ServerSuffix}__get_branch_authored_mcp_configs" });
    }

    [Test]
    public async Task A_non_aliasing_launch_admits_the_names_muse_shows_for_canonical_servers() {
        var admitted = Admitted(KcapMcpRegistry.ReservedResultChannelId, "kcap-sessions");

        await Assert.That(admitted.Contains("mcp__kcap_flow_result__submit_review_result")).IsTrue();
        await Assert.That(admitted.Contains(SearchTool)).IsTrue();
        await Assert.That(admitted.Contains("mcp__kcap_review__get_pr_summary")).IsFalse();
    }

    [Test]
    public async Task The_wire_key_is_the_server_name_with_underscores() =>
        await Assert.That(MuseUnattendedApprovals.ServerKey("kcap-flow-result-ab12")).IsEqualTo("kcap_flow_result_ab12");

    [Test]
    public async Task No_servers_admit_nothing() {
        await Assert.That(MuseUnattendedApprovals.AdmittedFor([], Aliasing)).IsSameReferenceAs(FrozenSet<string>.Empty);
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
