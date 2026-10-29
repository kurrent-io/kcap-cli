using Capacitor.Cli.Daemon.Services;
using Capacitor.Remote.Models;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public sealed class SpecialKeyMapTests {
    [Test]
    public async Task Escape_returns_ESC() {
        var bytes = SpecialKeyMap.ToBytes("Escape");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b });
    }

    [Test]
    public async Task Tab_returns_HT() {
        var bytes = SpecialKeyMap.ToBytes("Tab");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x09 });
    }

    [Test]
    public async Task Enter_returns_CR() {
        var bytes = SpecialKeyMap.ToBytes("Enter");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x0d });
    }

    [Test]
    public async Task CtrlC_returns_ETX() {
        var bytes = SpecialKeyMap.ToBytes("CtrlC");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x03 });
    }

    [Test]
    public async Task ArrowUp_returns_CSI_A() {
        var bytes = SpecialKeyMap.ToBytes("ArrowUp");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x41 });
    }

    [Test]
    public async Task ArrowDown_returns_CSI_B() {
        var bytes = SpecialKeyMap.ToBytes("ArrowDown");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x42 });
    }

    [Test]
    public async Task ShiftTab_returns_CSI_Z() {
        var bytes = SpecialKeyMap.ToBytes("ShiftTab");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x5a });
    }

    [Test]
    [Arguments(null)]
    [Arguments("BogusKey")]
    public async Task Unknown_key_returns_empty_array(string? key) {
        var bytes = SpecialKeyMap.ToBytes(key);
        await Assert.That(bytes.Length).IsEqualTo(0);
    }
    [Test]
    public async Task A_wheel_report_reaches_the_pty_without_becoming_a_prompt() {
        var report = "\x1b[<64;50;19M"u8.ToArray();
        await Assert.That(TerminalScrollKey.TryCreate(report, out var key)).IsTrue();
        await Assert.That(SpecialKeyMap.ToBytes(key)).IsEquivalentTo(report);
        await Assert.That(SpecialKeyMap.ToBytes("Wheel:" + Convert.ToBase64String("text\r"u8))).IsEmpty();
    }

}
