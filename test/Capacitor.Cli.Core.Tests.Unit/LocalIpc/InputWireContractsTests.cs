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

    [Test]
    public async Task Send_text_with_attachments_serializes_snake_case_with_every_member() =>
        await Assert.That(JsonSerializer.Serialize(
                new SendTextWithAttachmentsDto("a1", "hello", ["0123456789abcdef0123456789abcdef"]),
                InputIpcJsonContext.Default.SendTextWithAttachmentsDto))
            .IsEqualTo("""{"agent_id":"a1","text":"hello","attachment_ids":["0123456789abcdef0123456789abcdef"]}""");

    [Test]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1","text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":"x","attachment_ids":null}""")]
    public async Task Attachment_frame_without_ids_is_structurally_invalid(string json) {
        var dto = JsonSerializer.Deserialize(json, InputIpcJsonContext.Default.SendTextWithAttachmentsDto);
        await Assert.That(InputWire.IsStructurallyValid(dto)).IsFalse();
    }

    [Test]
    [Arguments("0123456789abcdef0123456789abcdef", true)]
    [Arguments("0123456789ABCDEF0123456789ABCDEF", true)]
    [Arguments("0123456789abcdef0123456789abcde", false)]
    [Arguments("0123456789abcdef0123456789abcdef0", false)]
    [Arguments("01234567-89ab-cdef-0123-456789abcdef", false)]
    [Arguments("../etc/passwd", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Attachment_id_is_a_guid_n(string? id, bool valid) =>
        await Assert.That(InputWire.IsValidAttachmentId(id)).IsEqualTo(valid);
}
