using System.Text.Json;
using Capacitor.Cli.Daemon.Harness.Muse;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseMspTests {
    const string Cmd = "01a13695-0736-75d0-8f4e-f121585b8453";
    const string Sid = "01a13695-6ad8-7313-9d2b-57f03aebbb24";

    static JsonDocument Parse(string line) => JsonDocument.Parse(line);

    // ---- MuseMspFrame.TryParse ----

    [Test]
    public async Task Initialize_response_is_a_response_with_id_and_result() {
        using var frame = MuseMspFrame.TryParse(MuseMspWire.InitializeResponse);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Id).IsEqualTo(1L);
        await Assert.That(frame.IsResponse).IsTrue();
        await Assert.That(frame.IsNotification).IsFalse();
        await Assert.That(frame.Method).IsNull();
        await Assert.That(frame.Error).IsNull();
        await Assert.That(frame.Result!.Value.Obj("schema")!.Value.Num("version")).IsEqualTo(1L);
    }

    [Test]
    public async Task Approval_requested_is_a_notification_with_object_requirement_id() {
        using var frame = MuseMspFrame.TryParse(MuseMspWire.ApprovalRequested);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.IsNotification).IsTrue();
        await Assert.That(frame.IsResponse).IsFalse();
        await Assert.That(frame.Method).IsEqualTo("approval/requested");
        await Assert.That(frame.Id).IsNull();
        await Assert.That(frame.Params!.Value.Str("approvalId")).IsNotNull();
        await Assert.That(frame.Params!.Value.Obj("currentRequirementId")).IsNotNull();
    }

    [Test]
    public async Task Error_response_exposes_error_member() {
        using var frame = MuseMspFrame.TryParse("""{"jsonrpc":"2.0","id":7,"error":{"code":-32057,"message":"bad"}}""");

        await Assert.That(frame!.IsResponse).IsTrue();
        await Assert.That(frame.Result).IsNull();
        await Assert.That(frame.Error!.Value.Num("code")).IsEqualTo(-32057L);
    }

    [Test]
    public async Task Server_request_with_id_and_method_is_neither_response_nor_notification() {
        using var frame = MuseMspFrame.TryParse("""{"jsonrpc":"2.0","id":3,"method":"approval/request","params":{}}""");

        await Assert.That(frame!.IsResponse).IsFalse();
        await Assert.That(frame.IsNotification).IsFalse();
        await Assert.That(frame.Method).IsEqualTo("approval/request");
    }

    [Test]
    [Arguments("garbage")]
    [Arguments("")]
    [Arguments("[1,2]")]
    [Arguments("42")]
    [Arguments("{\"id\":1")]
    public async Task Non_object_or_malformed_line_parses_to_null(string line) =>
        await Assert.That(MuseMspFrame.TryParse(line)).IsNull();

    [Test]
    public async Task Trailing_carriage_return_is_tolerated() {
        using var frame = MuseMspFrame.TryParse("""{"jsonrpc":"2.0","id":2,"result":{}}""" + "\r");

        await Assert.That(frame!.Id).IsEqualTo(2L);
    }

    // ---- MuseMsp.NewId ----

    [Test]
    public async Task NewId_is_a_version_7_guid() {
        var id = MuseMsp.NewId();

        await Assert.That(Guid.TryParseExact(id, "D", out var guid)).IsTrue();
        await Assert.That(id[14]).IsEqualTo('7');
        await Assert.That(guid.Version).IsEqualTo(7);
        await Assert.That(MuseMsp.NewId()).IsNotEqualTo(id);
    }

    // ---- request builders ----

    [Test]
    public async Task Initialize_carries_client_info_and_requested_capabilities() {
        using var doc = Parse(MuseMsp.Initialize(1, "1.2.3"));
        var root = doc.RootElement;
        var p    = root.Obj("params")!.Value;

        await Assert.That(root.Str("jsonrpc")).IsEqualTo("2.0");
        await Assert.That(root.Num("id")).IsEqualTo(1L);
        await Assert.That(root.Str("method")).IsEqualTo("initialize");
        await Assert.That(p.Obj("clientInfo")!.Value.Str("name")).IsEqualTo("kcap_daemon");
        await Assert.That(p.Obj("clientInfo")!.Value.Str("version")).IsEqualTo("1.2.3");
        await Assert.That(p.Obj("capabilities")!.Value.Arr("requestedCapabilities")!.Value.GetRawText())
            .IsEqualTo("""["sessionMcp"]""");
    }

    [Test]
    public async Task Initialized_is_a_notification_without_id() {
        using var doc = Parse(MuseMsp.Initialized());

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("initialized");
        await Assert.That(doc.RootElement.TryGetProperty("id", out _)).IsFalse();
    }

    [Test]
    public async Task RequestReceipt_is_an_empty_result_for_the_server_request_id() {
        using var frame = MuseMspFrame.TryParse(MuseMsp.RequestReceipt(7));

        await Assert.That(frame!.IsResponse).IsTrue();
        await Assert.That(frame.Id).IsEqualTo(7L);
        await Assert.That(frame.Result!.Value.GetRawText()).IsEqualTo("{}");
        await Assert.That(frame.Error).IsNull();
    }

    [Test]
    public async Task SessionStart_without_mcp_servers_has_no_config_member() {
        using var doc = Parse(MuseMsp.SessionStart(2, Cmd, Sid, "/work", "muse-spark-1.3", "meta", ":ask-me", "onRequest", null));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("session/start");
        await Assert.That(p.TryGetProperty("config", out _)).IsFalse();
        await Assert.That(p.Str("commandId")).IsEqualTo(Cmd);
        await Assert.That(p.Str("sessionId")).IsEqualTo(Sid);
        await Assert.That(p.Str("workspaceRoot")).IsEqualTo("/work");
        await Assert.That(p.Str("modelId")).IsEqualTo("muse-spark-1.3");
        await Assert.That(p.Str("providerId")).IsEqualTo("meta");
        await Assert.That(p.Str("permissionProfile")).IsEqualTo(":ask-me");
        await Assert.That(p.Str("approvalMode")).IsEqualTo("onRequest");
    }

    [Test]
    public async Task SessionStart_with_mcp_servers_nests_them_under_config() {
        var servers = new JsonObject {
            ["kcap_sessions"] = new JsonObject { ["transport"] = "stdio", ["command"] = "kcap" },
        };

        using var doc = Parse(MuseMsp.SessionStart(2, Cmd, Sid, "/work", null, "meta", ":read-only", "onRequest", servers));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(p.Obj("config")!.Value.Obj("mcpServers")!.Value.Obj("kcap_sessions")!.Value.Str("command")).IsEqualTo("kcap");
        await Assert.That(p.TryGetProperty("modelId", out _)).IsFalse();
    }

    [Test]
    public async Task SessionResume_carries_command_and_session_ids() {
        using var doc = Parse(MuseMsp.SessionResume(3, Cmd, Sid));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("session/resume");
        await Assert.That(p.Str("commandId")).IsEqualTo(Cmd);
        await Assert.That(p.Str("sessionId")).IsEqualTo(Sid);
    }

    [Test]
    public async Task TurnStart_sends_text_input() {
        using var doc = Parse(MuseMsp.TurnStart(4, Cmd, Sid, "hello"));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("turn/start");
        await Assert.That(p.Arr("input")!.Value.GetRawText()).IsEqualTo("""[{"type":"text","text":"hello"}]""");
    }

    [Test]
    public async Task TurnSteer_carries_expected_turn_id_and_text_input() {
        using var doc = Parse(MuseMsp.TurnSteer(5, Cmd, Sid, "turn-1", "go left"));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("turn/steer");
        await Assert.That(p.Str("expectedTurnId")).IsEqualTo("turn-1");
        await Assert.That(p.Arr("input")!.Value.GetRawText()).IsEqualTo("""[{"type":"text","text":"go left"}]""");
    }

    [Test]
    public async Task TurnCancel_carries_turn_id() {
        using var doc = Parse(MuseMsp.TurnCancel(6, Cmd, Sid, "turn-1"));

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("turn/cancel");
        await Assert.That(doc.RootElement.Obj("params")!.Value.Str("turnId")).IsEqualTo("turn-1");
    }

    [Test]
    public async Task ApprovalDecide_passes_an_object_requirement_id_through_unchanged() {
        using var request = MuseMspFrame.TryParse(MuseMspWire.ApprovalRequested);
        var requirement   = request!.Params!.Value.Obj("currentRequirementId")!.Value;

        using var doc = Parse(MuseMsp.ApprovalDecide(7, Cmd, Sid, "approval-1", requirement, "allow_once", null));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("approval/decide");
        await Assert.That(p.Str("approvalId")).IsEqualTo("approval-1");
        await Assert.That(p.Str("choiceId")).IsEqualTo("allow_once");
        await Assert.That(p.Prop("requirementId")!.Value.GetRawText()).IsEqualTo(requirement.GetRawText());
        await Assert.That(p.TryGetProperty("feedback", out _)).IsFalse();
    }

    [Test]
    public async Task ApprovalDecide_passes_a_string_requirement_id_and_feedback() {
        using var req = JsonDocument.Parse("\"req-9\"");

        using var doc = Parse(MuseMsp.ApprovalDecide(7, Cmd, Sid, "approval-1", req.RootElement, "abort", "not now"));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(p.Str("requirementId")).IsEqualTo("req-9");
        await Assert.That(p.Str("feedback")).IsEqualTo("not now");
    }

    [Test]
    public async Task UserInputAnswer_emits_exactly_one_value_member_per_answer() {
        using var doc = Parse(MuseMsp.UserInputAnswer(8, Cmd, Sid, "input-1", [
            new MuseUserInputAnswer("q1", SelectedLabel: "red"),
            new MuseUserInputAnswer("q2", SelectedLabels: ["a", "b"]),
            new MuseUserInputAnswer("q3", FreeText: "green"),
        ]));
        var p = doc.RootElement.Obj("params")!.Value;

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("userInput/answer");
        await Assert.That(p.Str("userInputId")).IsEqualTo("input-1");
        await Assert.That(p.Arr("answers")!.Value.GetRawText()).IsEqualTo(
            """[{"questionId":"q1","selectedLabel":"red"},{"questionId":"q2","selectedLabels":["a","b"]},{"questionId":"q3","freeText":"green"}]""");
    }

    [Test]
    public async Task UserInputCancel_carries_user_input_id() {
        using var doc = Parse(MuseMsp.UserInputCancel(9, Cmd, Sid, "input-1"));

        await Assert.That(doc.RootElement.Str("method")).IsEqualTo("userInput/cancel");
        await Assert.That(doc.RootElement.Obj("params")!.Value.Str("userInputId")).IsEqualTo("input-1");
    }

    // ---- FakeMuseMspProcess ----

    [Test]
    public async Task Fake_auto_answers_initialize_with_the_request_id() {
        await using var fake = new FakeMuseMspProcess();

        await fake.WriteLineAsync(MuseMsp.Initialize(41, "1.0.0"), CancellationToken.None);
        fake.EndOfStream();

        var lines = new List<string>();
        await foreach (var l in fake.ReadLinesAsync(CancellationToken.None)) lines.Add(l);

        using var frame = MuseMspFrame.TryParse(lines.Single());

        await Assert.That(frame!.Id).IsEqualTo(41L);
        await Assert.That(frame.Result!.Value.Obj("schema")!.Value.Num("version")).IsEqualTo(1L);
        await Assert.That(fake.Writes.Count).IsEqualTo(1);
    }
}
