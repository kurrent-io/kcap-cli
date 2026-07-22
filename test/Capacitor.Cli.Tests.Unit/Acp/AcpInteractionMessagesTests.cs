// test/Capacitor.Cli.Tests.Unit/Acp/AcpInteractionMessagesTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Round-trip / wire-shape tests for the Task B1 ACP permission + elicitation DTOs — proves
/// the source-gen <see cref="CapacitorJsonContext"/> registrations exist and serialize with the
/// exact camelCase wire vocabulary (<see cref="Acp.PermissionOutcomeDto"/>'s <c>"selected"</c> /
/// <c>"cancelled"</c> spellings, distinct from the server-internal <c>"cancel"</c>) and snake_case
/// server-contract-mirror field names required for daemon&lt;-&gt;server lockstep (Task A2).
/// </summary>
public class AcpInteractionMessagesTests {
    [Test]
    public async Task SessionRequestPermissionParams_round_trips_with_camelCase_wire_shape() {
        var toolCall = JsonDocument.Parse("""{"name":"bash","input":{"command":"ls"}}""").RootElement.Clone();
        var src = new SessionRequestPermissionParams(
            SessionId: "sess-1",
            ToolCall:  toolCall,
            Options: [
                new PermissionOptionDto("opt-allow", "Allow", "allow_once"),
                new PermissionOptionDto("opt-deny",  "Deny",  "reject_once")
            ]
        );

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.SessionRequestPermissionParams);

        await Assert.That(json).Contains(@"""sessionId""");
        await Assert.That(json).Contains(@"""toolCall""");
        await Assert.That(json).Contains(@"""options""");
        await Assert.That(json).Contains(@"""optionId""");
        await Assert.That(json).Contains(@"""name""");
        await Assert.That(json).Contains(@"""kind""");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SessionRequestPermissionParams)!;
        await Assert.That(back.SessionId).IsEqualTo("sess-1");
        await Assert.That(back.Options[0].OptionId).IsEqualTo("opt-allow");
        await Assert.That(back.Options[1].Kind).IsEqualTo("reject_once");
        await Assert.That(back.ToolCall.GetProperty("name").GetString()).IsEqualTo("bash");
    }

    [Test]
    public async Task PermissionOutcomeDto_selected_serializes_optionId_and_omits_it_when_cancelled() {
        var selected = new PermissionOutcomeDto("selected", "opt-allow");
        var selectedJson = JsonSerializer.Serialize(selected, CapacitorJsonContext.Default.PermissionOutcomeDto);
        await Assert.That(selectedJson).Contains(@"""outcome"":""selected""");
        await Assert.That(selectedJson).Contains(@"""optionId"":""opt-allow""");

        // Fail-safe cancellation vocabulary: the ACP wire spelling is "cancelled" (double-L),
        // distinct from the server's internal InterruptOutcomes.Cancel = "cancel".
        var cancelled = new PermissionOutcomeDto("cancelled");
        var cancelledJson = JsonSerializer.Serialize(cancelled, CapacitorJsonContext.Default.PermissionOutcomeDto);
        await Assert.That(cancelledJson).Contains(@"""outcome"":""cancelled""");
        await Assert.That(cancelledJson).DoesNotContain(@"""optionId""");

        var back = JsonSerializer.Deserialize(cancelledJson, CapacitorJsonContext.Default.PermissionOutcomeDto)!;
        await Assert.That(back.Outcome).IsEqualTo("cancelled");
        await Assert.That(back.OptionId).IsNull();
    }

    [Test]
    public async Task PermissionOutcomeResult_round_trips_wrapping_outcome() {
        var src  = new PermissionOutcomeResult(new PermissionOutcomeDto("selected", "opt-1"));
        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.PermissionOutcomeResult);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.PermissionOutcomeResult)!;

        await Assert.That(back.Outcome.Outcome).IsEqualTo("selected");
        await Assert.That(back.Outcome.OptionId).IsEqualTo("opt-1");
    }

    [Test]
    public async Task ElicitationCreateParams_omits_optional_fields_when_null_and_round_trips_when_present() {
        var minimal = new ElicitationCreateParams("sess-1", "Pick one");
        var minimalJson = JsonSerializer.Serialize(minimal, CapacitorJsonContext.Default.ElicitationCreateParams);
        await Assert.That(minimalJson).DoesNotContain(@"""options""");
        await Assert.That(minimalJson).DoesNotContain(@"""requestedSchema""");

        var schema = JsonDocument.Parse("""{"type":"string"}""").RootElement.Clone();
        var full = new ElicitationCreateParams(
            "sess-1", "Pick one",
            Options: [new PermissionOptionDto("opt-a", "A", "allow_once")],
            RequestedSchema: schema
        );
        var fullJson = JsonSerializer.Serialize(full, CapacitorJsonContext.Default.ElicitationCreateParams);
        await Assert.That(fullJson).Contains(@"""requestedSchema""");
        await Assert.That(fullJson).Contains(@"""options""");

        var back = JsonSerializer.Deserialize(fullJson, CapacitorJsonContext.Default.ElicitationCreateParams)!;
        await Assert.That(back.Message).IsEqualTo("Pick one");
        await Assert.That(back.Options![0].OptionId).IsEqualTo("opt-a");
        await Assert.That(back.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("string");
    }

    [Test]
    public async Task ElicitationCreateResult_round_trips() {
        var src  = new ElicitationCreateResult(new PermissionOutcomeDto("cancelled"));
        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.ElicitationCreateResult);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ElicitationCreateResult)!;

        await Assert.That(back.Outcome.Outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AcpInteractionRequest_round_trips_and_uses_snake_case_server_contract_wire_shape() {
        var toolInput = JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone();
        var schema    = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        var src = new AcpInteractionRequest(
            AgentId:       "agent-1",
            AcpSessionId:  "acp-sess-1",
            Kind:          "permission",
            ToolName:      "bash",
            ToolInput:     toolInput,
            ToolCallId:    "call-1",
            Prompt:        null,
            Options:       [new AcpInteractionOption("opt-1", "Allow", "Allow once", "allow_once")],
            IsMultiSelect: false,
            RequestedSchema: schema
        );

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.AcpInteractionRequest);

        // Server-contract mirror types use the context's default snake_case naming policy
        // (matching HostedPermissionRequest/PermissionResolution precedent), NOT the explicit
        // camelCase JsonPropertyName vocabulary used by the spec-derived Acp.* wire DTOs above.
        await Assert.That(json).Contains(@"""agent_id""");
        await Assert.That(json).Contains(@"""acp_session_id""");
        await Assert.That(json).Contains(@"""tool_name""");
        await Assert.That(json).Contains(@"""tool_input""");
        await Assert.That(json).Contains(@"""tool_call_id""");
        await Assert.That(json).Contains(@"""is_multi_select""");
        await Assert.That(json).Contains(@"""requested_schema""");
        await Assert.That(json).Contains(@"""option_id""");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionRequest)!;
        await Assert.That(back.AgentId).IsEqualTo("agent-1");
        await Assert.That(back.Options![0].OptionId).IsEqualTo("opt-1");
        await Assert.That(back.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task AcpInteractionDecision_and_resolution_round_trip() {
        var updatedInput = JsonDocument.Parse("""{"command":"ls -la"}""").RootElement.Clone();
        var decision = new AcpInteractionDecision(
            Outcome:             "selected",
            SelectedOptionId:    "opt-1",
            SelectedOptionLabel: "Allow",
            SelectedIndex:       0,
            FreeText:            null,
            UpdatedToolInput:    updatedInput
        );
        var resolution = new AcpInteractionResolution("req-1", decision);

        var json = JsonSerializer.Serialize(resolution, CapacitorJsonContext.Default.AcpInteractionResolution);
        await Assert.That(json).Contains(@"""request_id""");
        await Assert.That(json).Contains(@"""selected_option_id""");
        await Assert.That(json).Contains(@"""selected_option_label""");
        await Assert.That(json).Contains(@"""updated_tool_input""");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionResolution)!;
        await Assert.That(back.RequestId).IsEqualTo("req-1");
        await Assert.That(back.Decision.SelectedOptionId).IsEqualTo("opt-1");
        await Assert.That(back.Decision.UpdatedToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls -la");
    }
}
