using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The hub client deserializes the <c>AgentRegisteredAcked</c> result through the AOT context, so the
/// type must be registered there and read the server's snake_case shape.</summary>
public class AgentRegisteredResponseWireTests {
    [Test]
    public async Task A_refusal_round_trips_through_the_aot_context_in_snake_case() {
        var refused = new AgentRegisteredResponse(false, "ownership_fence");

        var json = JsonSerializer.Serialize(refused, CapacitorJsonContext.Default.AgentRegisteredResponse);

        await Assert.That(json).IsEqualTo("""{"registered":false,"refused_reason":"ownership_fence"}""");
        await Assert.That(JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AgentRegisteredResponse)).IsEqualTo(refused);
    }

    [Test]
    public async Task An_acceptance_without_a_reason_reads_as_registered() {
        var accepted = JsonSerializer.Deserialize("""{"registered":true}""", CapacitorJsonContext.Default.AgentRegisteredResponse);

        await Assert.That(accepted).IsEqualTo(AgentRegisteredResponse.Accepted);
    }
}
