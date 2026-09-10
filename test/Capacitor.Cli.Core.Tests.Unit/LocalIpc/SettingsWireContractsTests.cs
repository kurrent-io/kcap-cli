using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class SettingsWireContractsTests {
    [Test]
    public async Task Put_serializes_snake_case() =>
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsPutDto(2), SettingsIpcJsonContext.Default.DaemonSettingsPutDto))
            .IsEqualTo("""{"max_agents":2}""");

    [Test]
    public async Task Ack_serializes_every_member() {
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsAckDto(true, null, 2), SettingsIpcJsonContext.Default.DaemonSettingsAckDto))
            .IsEqualTo("""{"ok":true,"reason":null,"max_agents":2}""");
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsAckDto(false, DaemonSettingsReasons.InvalidMaxAgents, 5), SettingsIpcJsonContext.Default.DaemonSettingsAckDto))
            .IsEqualTo("""{"ok":false,"reason":"invalid_max_agents","max_agents":5}""");
    }

    [Test]
    public async Task Ack_without_max_agents_deserializes_to_null() {
        var ack = JsonSerializer.Deserialize("""{"ok":true,"reason":null}""", SettingsIpcJsonContext.Default.DaemonSettingsAckDto)!;
        await Assert.That(ack.Ok).IsTrue();
        await Assert.That(ack.MaxAgents).IsNull();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"max_agents":null}""")]
    public async Task A_put_with_no_setting_has_nothing_to_apply(string json) {
        var dto = JsonSerializer.Deserialize(json, SettingsIpcJsonContext.Default.DaemonSettingsPutDto);
        await Assert.That(SettingsWire.HasAnySetting(dto)).IsFalse();
    }

    [Test]
    public async Task Null_is_not_a_put() =>
        await Assert.That(SettingsWire.HasAnySetting(null)).IsFalse();

    [Test]
    public async Task Capability_string_is_settings_1() =>
        await Assert.That(SettingsWire.Capability).IsEqualTo("settings/1");
}
