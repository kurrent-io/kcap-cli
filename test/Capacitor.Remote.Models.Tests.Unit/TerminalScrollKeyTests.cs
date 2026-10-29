using System.Text;

namespace Capacitor.Remote.Models.Tests.Unit;

public class TerminalScrollKeyTests {
    [Test]
    [Arguments("\x1b[<64;1;1M")]
    [Arguments("\x1b[<65;500;200M")]
    [Arguments("\x1b[M`!!")]
    [Arguments("\x1b[Ma!!")]
    public async Task Wheel_reports_round_trip_without_changing_their_coordinates(string report) {
        var bytes = Encoding.UTF8.GetBytes(report);
        await Assert.That(TerminalScrollKey.TryCreate(bytes, out var key)).IsTrue();
        await Assert.That(TerminalScrollKey.TryDecode(key, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEquivalentTo(bytes);
    }

    [Test]
    [Arguments((byte)233, (byte)33)]
    [Arguments((byte)33, (byte)233)]
    [Arguments(byte.MaxValue, byte.MaxValue)]
    public async Task X10_coordinates_use_the_full_byte_range(byte column, byte row) {
        byte[] report = [0x1b, (byte)'[', (byte)'M', 96, column, row];
        await Assert.That(TerminalScrollKey.TryCreate(report, out var key)).IsTrue();
        await Assert.That(TerminalScrollKey.TryDecode(key, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEquivalentTo(report);
    }

    [Test]
    [Arguments("text\r")]
    [Arguments("\x1b[<0;1;1M")]
    [Arguments("\x1b[<64;0;1M")]
    [Arguments("\x1b[<64;501;1M")]
    [Arguments("\x1b[<64;1;201M")]
    [Arguments("\x1b[<64;1;1M\r")]
    [Arguments("\x1b[<64;+1;1M")]
    [Arguments("\x1b[<64;1;1m")]
    public async Task The_wire_cannot_carry_typing_clicks_or_submit_keys(string report) {
        var bytes = Encoding.UTF8.GetBytes(report);
        await Assert.That(TerminalScrollKey.TryCreate(bytes, out _)).IsFalse();
        await Assert.That(TerminalScrollKey.TryDecode("Wheel:" + Convert.ToBase64String(bytes), out _)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("Wheel:bad-base64")]
    [Arguments("Wheel:")]
    [Arguments("Wheel:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task An_invalid_envelope_is_rejected(string? key) =>
        await Assert.That(TerminalScrollKey.TryDecode(key, out _)).IsFalse();
}
