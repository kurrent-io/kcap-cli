using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class InputWireContractsTests {
    [Test]
    public async Task Send_text_serializes_snake_case() =>
        await Assert.That(JsonSerializer.Serialize(new SendTextDto("a1", "hello"), InputIpcJsonContext.Default.SendTextDto))
            .IsEqualTo("""{"agent_id":"a1","text":"hello"}""");

    [Test]
    public async Task Ack_serializes_every_member_with_outcome_last() {
        await Assert.That(JsonSerializer.Serialize(new SendTextAckDto(true, null, null, SendTextOutcomes.Delivered), InputIpcJsonContext.Default.SendTextAckDto))
            .IsEqualTo("""{"ok":true,"reason":null,"error":null,"outcome":"delivered"}""");
        await Assert.That(JsonSerializer.Serialize(new SendTextAckDto(false, SendTextReasons.QueueFull, "full", null), InputIpcJsonContext.Default.SendTextAckDto))
            .IsEqualTo("""{"ok":false,"reason":"queue_full","error":"full","outcome":null}""");
    }

    [Test]
    public async Task Ack_without_outcome_deserializes_to_null_outcome() {
        var ack = JsonSerializer.Deserialize("""{"ok":true,"reason":null,"error":null}""", InputIpcJsonContext.Default.SendTextAckDto)!;
        await Assert.That(ack.Ok).IsTrue();
        await Assert.That(ack.Outcome).IsNull();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1"}""")]
    [Arguments("""{"agent_id":null,"text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":null}""")]
    public async Task Missing_or_null_members_are_structurally_invalid(string json) {
        var dto = JsonSerializer.Deserialize(json, InputIpcJsonContext.Default.SendTextDto);
        await Assert.That(InputWire.IsStructurallyValid(dto)).IsFalse();
    }

    [Test]
    public async Task Empty_text_is_structurally_valid_so_the_handler_can_name_it() =>
        await Assert.That(InputWire.IsStructurallyValid(new SendTextDto("a1", ""))).IsTrue();
}
