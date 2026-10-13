using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

/// <summary>
/// Which permission frames a Grok reviewer approves unattended, against <c>toolCall</c> objects shaped as
/// Grok 1.0.52 sends them (<c>docs/probes/2026-10-13-grok-acp/</c>): only a <c>use_tool</c> call on a server
/// the launch injected, never an edit or a command.
/// </summary>
public class GrokToolAdmissionTests {
    const string Injected = "kcap-flow-result";

    static readonly IReadOnlySet<string> Admitted = new HashSet<string>(StringComparer.Ordinal) { Injected };

    static JsonElement UseTool(string toolName, string? title = null, string metaName = "use_tool", string variant = "UseTool") =>
        JsonDocument.Parse($$"""
            {"toolCallId":"call-1","kind":"other","title":"{{title ?? toolName}}",
             "rawInput":{"variant":"{{variant}}","tool_name":"{{toolName}}","tool_input":{} },
             "_meta":{"x.ai/tool":{"version":1,"name":"{{metaName}}","kind":"use_tool","namespace":"grok_build"} } }
            """).RootElement.Clone();

    static readonly JsonElement ShellWrite = JsonDocument.Parse("""
        {"toolCallId":"call-2","kind":"execute","title":"Execute `echo changed > notes.txt`",
         "rawInput":{"variant":"Bash","command":"echo changed > notes.txt","is_background":false},
         "_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","namespace":"grok_build"}}}
        """).RootElement.Clone();

    static readonly JsonElement FileWrite = JsonDocument.Parse("""
        {"toolCallId":"call-3","kind":"edit","title":"Write `/repo/notes.txt`",
         "rawInput":{"variant":"Write","file_path":"/repo/notes.txt","content":"hi"},
         "_meta":{"x.ai/tool":{"version":1,"name":"write","kind":"write","namespace":"opencode"}}}
        """).RootElement.Clone();

    [Test]
    public async Task A_call_on_an_injected_server_is_admitted() {
        await Assert.That(GrokToolAdmission.IsAdmitted(UseTool($"{Injected}__submit_review_result"), Admitted)).IsTrue();
    }

    [Test]
    public async Task Edits_and_commands_are_not_admitted() {
        await Assert.That(GrokToolAdmission.IsAdmitted(ShellWrite, Admitted)).IsFalse();
        await Assert.That(GrokToolAdmission.IsAdmitted(FileWrite, Admitted)).IsFalse();
    }

    [Test]
    [Arguments("kcap-flows__start_flow")]                        // a server the launch did not inject
    [Arguments("kcap-flow__submit_review_result")]               // a prefix of the injected name
    [Arguments("other__kcap-flow-result__submit_review_result")] // the injected name further in
    [Arguments("kcap-flow-result__")]                            // no tool
    [Arguments("kcap-flow-result")]                              // no separator
    public async Task A_call_on_any_other_server_is_not_admitted(string toolName) {
        await Assert.That(GrokToolAdmission.IsAdmitted(UseTool(toolName), Admitted)).IsFalse();
    }

    /// <summary>Every structural field has to agree; a frame that only looks like a tool call in one of
    /// them is not one.</summary>
    [Test]
    public async Task A_frame_whose_fields_disagree_is_not_admitted() {
        var name = $"{Injected}__submit_review_result";

        await Assert.That(GrokToolAdmission.IsAdmitted(UseTool(name, title: $"Running {name}"), Admitted)).IsFalse();
        await Assert.That(GrokToolAdmission.IsAdmitted(UseTool(name, metaName: "run_terminal_command"), Admitted)).IsFalse();
        await Assert.That(GrokToolAdmission.IsAdmitted(UseTool(name, variant: "Bash"), Admitted)).IsFalse();
    }

    [Test]
    public async Task The_admitted_set_is_the_injected_servers() {
        IReadOnlyList<AcpMcpServerSpec> injected = [
            new(Injected, "/usr/local/bin/kcap", ["mcp", "flow-result"], []),
            new("kcap-review", "/usr/local/bin/kcap", ["mcp", "review"], []),
        ];

        await Assert.That(GrokToolAdmission.AdmittedFor(injected, LaunchIdentity.ForLaunch(false)))
            .IsEquivalentTo(new[] { Injected, "kcap-review" });
    }

    /// <summary>Through the bridge with Grok's matcher: the result call is approved and a command is
    /// reaped, as the allowlisted policy reaps any frame it does not admit.</summary>
    [Test]
    public async Task The_bridge_approves_the_result_call_and_reaps_a_command() {
        var reaped = new List<string>();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (_, _) => throw new InvalidOperationException("an unattended reviewer never routes to a human"),
            agentId: "agent-1",
            logger: NullLogger.Instance,
            time: TimeProvider.System,
            unattendedPolicy: AcpUnattendedInteractionPolicy.AllowlistedAutoApprove,
            unexpectedUnattendedInteraction: reaped.Add,
            admittedToolIds: Admitted,
            admissionMatcher: GrokToolAdmission.IsAdmitted);

        const string options = """[{"optionId":"allow-edits-session","name":"Always","kind":"allow_always"},{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"reject-once","name":"No","kind":"reject_once"}]""";

        var approved = await bridge.HandleAsync(new AcpRequest(1, "session/request_permission", Params(UseTool($"{Injected}__submit_review_result"), options)), CancellationToken.None);
        await Assert.That(approved!.Value.GetProperty("outcome").GetProperty("optionId").GetString()).IsEqualTo("allow-once");
        await Assert.That(reaped).IsEmpty();

        var refused = await bridge.HandleAsync(new AcpRequest(2, "session/request_permission", Params(ShellWrite, options)), CancellationToken.None);
        await Assert.That(refused!.Value.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(reaped).Count().IsEqualTo(1);
    }

    static JsonElement Params(JsonElement toolCall, string optionsJson) =>
        JsonDocument.Parse($$"""{"sessionId":"s-1","toolCall":{{toolCall.GetRawText()}},"options":{{optionsJson}}}""").RootElement.Clone();
}
