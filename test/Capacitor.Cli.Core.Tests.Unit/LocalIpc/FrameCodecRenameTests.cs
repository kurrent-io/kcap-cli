using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecRenameTests {
    [Test]
    [Arguments(FrameType.SessionRename)]
    [Arguments(FrameType.SessionRenameAck)]
    public async Task Codec_round_trips_SessionRename_and_ack(FrameType type) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.RenameJson(type, """{"k":"v"}"""), CancellationToken.None);
        ms.Position = 0;
        var f = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Rename_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.SessionRename).IsEqualTo((byte)33);
        await Assert.That((byte)FrameType.SessionRenameAck).IsEqualTo((byte)88);
#pragma warning restore TUnitAssertions0005
    }
}
