using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class RenameWireContractsTests {
    [Test]
    public async Task Rename_dto_round_trips_snake_case() {
        const string json = """{"agent_id":"a","title":"T","expected_user_subject":"u"}""";
        var dto = JsonSerializer.Deserialize(json, RenameIpcJsonContext.Default.SessionRenameDto)!;
        await Assert.That(dto).IsEqualTo(new SessionRenameDto("a", "T", "u"));
        await Assert.That(JsonSerializer.Serialize(dto, RenameIpcJsonContext.Default.SessionRenameDto)).IsEqualTo(json);
    }

    [Test]
    public async Task Rename_ack_emits_every_member() =>
        await Assert.That(JsonSerializer.Serialize(new SessionRenameAckDto(false, SessionRenameReasons.Refused, null), RenameIpcJsonContext.Default.SessionRenameAckDto))
            .IsEqualTo("""{"ok":false,"reason":"refused","title":null}""");

    [Test]
    public async Task NormalizeTitle_trims_caps_and_blanks() {
        await Assert.That(RenameWire.NormalizeTitle("  x  ")).IsEqualTo("x");
        await Assert.That(RenameWire.NormalizeTitle("   ")).IsNull();
        await Assert.That(RenameWire.NormalizeTitle(null)).IsNull();
        await Assert.That(RenameWire.NormalizeTitle(new string('a', 130))!.Length).IsEqualTo(120);
    }

    [Test]
    public async Task Capability_string_is_rename_1() =>
        await Assert.That(RenameWire.Capability).IsEqualTo("rename/1");
}
