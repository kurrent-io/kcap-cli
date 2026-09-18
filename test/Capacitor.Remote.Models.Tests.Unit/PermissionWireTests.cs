using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class PermissionWireTests {
    [Test]
    public async Task Response_payload_writes_snake_case_and_omits_unset_members() {
        var payload = new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, SelectedOptionIds = ["opt-a", "opt-b"], SelectedOptionLabels = ["A", "B"] };
        var json = JsonSerializer.Serialize(payload, RemoteModelsJsonContext.Default.PermissionResponsePayload);
        await Assert.That(json).IsEqualTo("""{"behavior":"answered","selected_option_ids":["opt-a","opt-b"],"selected_option_labels":["A","B"]}""");
    }

    [Test]
    public async Task Acp_option_reads_the_hub_shape_with_bounds() {
        var options = JsonSerializer.Deserialize(
            """[{"option_id":"a","label":"Yes","description":null,"kind":"allow_once","min_selections":1,"max_selections":2}]""",
            RemoteModelsJsonContext.Default.AcpInteractionOptionArray)!;
        await Assert.That(options[0].OptionId).IsEqualTo("a");
        await Assert.That(options[0].Kind).IsEqualTo("allow_once");
        await Assert.That(options[0].MaxSelections).IsEqualTo(2);
    }

    [Test]
    public async Task Session_detail_reads_events_with_payload_and_data() {
        var detail = JsonSerializer.Deserialize(
            """{"session_id":"s1","ended_at":null,"last_event_number":3,"events":[{"event_type":"InterruptIssued","event_number":3,"payload":{"request_id":"r1","kind":"permission"},"data":{"request_id":"r1"}}]}""",
            RemoteModelsJsonContext.Default.SessionDetailDto)!;
        await Assert.That(detail.LastEventNumber).IsEqualTo(3L);
        await Assert.That(detail.Events![0].EventType).IsEqualTo("InterruptIssued");
        await Assert.That(detail.Events[0].Payload!.Value.GetProperty("kind").GetString()).IsEqualTo("permission");
    }
}
