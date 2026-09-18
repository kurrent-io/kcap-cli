using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins that a daemon on this branch always advertises eval protocol 2 on <c>DaemonConnect</c>,
/// serialized through the same <c>CapacitorJsonContext</c>-backed SignalR JSON options
/// <c>ServerConnection</c> uses (snake_case, per the server's <c>JsonDefaults.ConfigureSignalRPayload</c>).
/// The checked-in fixture is the golden artifact shared with a server-side test that deserializes
/// it into <c>DaemonConnectArgs</c> and asserts <c>EvalProtocolVersion == 2</c>.
/// </summary>
public class DaemonConnectProtocolVersionTests {
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "daemon-connect.v2.json");

    static DaemonConnect BuildConnect(int evalProtocolVersion) => new(
        Name: "tony-daemon",
        Platform: "macOS 15.0 Arm64",
        RepoPaths: ["/Users/tony/dev/kcap-server"],
        MaxAgents: 5,
        LiveAgentIds: [],
        InstanceId: "11111111-1111-1111-1111-111111111111",
        Version: "1.2.3-sha-abc1234",
        SupportedVendors: ["claude", "codex"],
        MachineId: "machine-1",
        EvalProtocolVersion: evalProtocolVersion
    );

    [Test]
    public async Task Serialized_DaemonConnect_carries_eval_protocol_version_2() {
        var json = JsonSerializer.Serialize(BuildConnect(2), CapacitorJsonContext.Default.DaemonConnect);

        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("eval_protocol_version").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task A_daemon_predating_the_field_serializes_as_protocol_1() {
        var json = JsonSerializer.Serialize(BuildConnect(1), CapacitorJsonContext.Default.DaemonConnect);

        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("eval_protocol_version").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Golden_fixture_round_trips_into_DaemonConnect_at_protocol_2() {
        var fixtureJson = await File.ReadAllTextAsync(FixturePath);

        var connect = JsonSerializer.Deserialize(fixtureJson, CapacitorJsonContext.Default.DaemonConnect);
        await Assert.That(connect.EvalProtocolVersion).IsEqualTo(2);
        await Assert.That(connect.Name).IsEqualTo("tony-daemon");
        await Assert.That(connect.SupportedVendors).IsEquivalentTo(["claude", "codex"]);

        // A fresh serialization of the same shape carries the same field structure — a change to
        // the wire shape shows up here before it reaches the (separate) server-side fixture copy.
        var freshJson = JsonSerializer.Serialize(BuildConnect(2), CapacitorJsonContext.Default.DaemonConnect);
        using var freshDoc   = JsonDocument.Parse(freshJson);
        using var fixtureDoc = JsonDocument.Parse(fixtureJson);
        await Assert.That(freshDoc.RootElement.EnumerateObject().Select(p => p.Name))
            .IsEquivalentTo(fixtureDoc.RootElement.EnumerateObject().Select(p => p.Name));
    }
}
