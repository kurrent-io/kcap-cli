using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecInputTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.SendText)]
    [Arguments(FrameType.SendTextAck)]
    public async Task Input_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.InputJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Input_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.SendText).IsEqualTo((byte)22);
        await Assert.That((byte)FrameType.SendTextAck).IsEqualTo((byte)80);
#pragma warning restore TUnitAssertions0005
    }
}
