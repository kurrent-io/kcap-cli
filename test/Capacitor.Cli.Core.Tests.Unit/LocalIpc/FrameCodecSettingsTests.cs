using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecSettingsTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.DaemonSettingsPut)]
    [Arguments(FrameType.DaemonSettingsAck)]
    public async Task Settings_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.SettingsJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Settings_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.DaemonSettingsPut).IsEqualTo((byte)23);
        await Assert.That((byte)FrameType.DaemonSettingsAck).IsEqualTo((byte)81);
#pragma warning restore TUnitAssertions0005
    }
}
