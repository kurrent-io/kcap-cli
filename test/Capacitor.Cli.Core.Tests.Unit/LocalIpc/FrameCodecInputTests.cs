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

    [Test]
    public async Task Send_text_with_attachments_frame_round_trips_and_is_24() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.SendTextWithAttachments).IsEqualTo((byte)24);
#pragma warning restore TUnitAssertions0005
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.InputJson(FrameType.SendTextWithAttachments, """{"agent_id":"a"}"""), CancellationToken.None);
        ms.Position = 0;
        var read = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(read!.Type).IsEqualTo(FrameType.SendTextWithAttachments);
        await Assert.That(read.Text).IsEqualTo("""{"agent_id":"a"}""");
    }
}
