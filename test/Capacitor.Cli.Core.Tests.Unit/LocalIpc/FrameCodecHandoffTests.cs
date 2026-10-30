using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecHandoffTests {
    [Test]
    [Arguments(FrameType.HandoffProbe)]
    [Arguments(FrameType.HandoffProbeReply)]
    [Arguments(FrameType.HandoffOutcomeQuery)]
    [Arguments(FrameType.HandoffOutcomeReply)]
    public async Task Codec_round_trips_HandoffProbe_and_reply(FrameType type) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.HandoffJson(type, """{"k":"v"}"""), CancellationToken.None);
        ms.Position = 0;
        var f = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Handoff_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.HandoffProbe).IsEqualTo((byte)34);
        await Assert.That((byte)FrameType.HandoffProbeReply).IsEqualTo((byte)89);
        await Assert.That((byte)FrameType.HandoffOutcomeQuery).IsEqualTo((byte)35);
        await Assert.That((byte)FrameType.HandoffOutcomeReply).IsEqualTo((byte)90);
#pragma warning restore TUnitAssertions0005
        await Assert.That(HandoffWire.Capability).IsEqualTo("handoff/1");
    }
}
