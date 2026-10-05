using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins the hub wire names of <see cref="AgentRegistered"/>'s start-title fields.</summary>
public class AgentRegisteredWireFormatTests {
    [Test]
    public async Task Start_title_serializes_as_title_and_title_derived() {
        var reg = new AgentRegistered("a1", null, "opus", null, "/r", Title: "Fix login", TitleDerived: true);

        var json = JsonSerializer.Serialize(reg, CapacitorJsonContext.Default.AgentRegistered);

        await Assert.That(json).Contains("\"title\":\"Fix login\"");
        await Assert.That(json).Contains("\"title_derived\":true");
    }

    [Test]
    public async Task Payload_without_title_fields_reads_as_no_title() {
        var reg = JsonSerializer.Deserialize(
            """{"agent_id":"a1","model":"opus","repo_path":"/r"}""", CapacitorJsonContext.Default.AgentRegistered);

        await Assert.That(reg.Title).IsNull();
        await Assert.That(reg.TitleDerived).IsFalse();
    }
}
