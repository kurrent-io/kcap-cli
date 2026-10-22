using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// Only named keys and bounded wheel reports cross the remote terminal input lane.
public class SpecialKeyMapperTests {
    [Test]
    [Arguments(new byte[] { 0x1b }, "Escape")]
    [Arguments(new byte[] { 0x09 }, "Tab")]
    [Arguments(new byte[] { 0x0d }, "Enter")]
    [Arguments(new byte[] { 0x0a }, "Enter")]
    [Arguments(new byte[] { 0x03 }, "CtrlC")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x41 }, "ArrowUp")]
    [Arguments(new byte[] { 0x1b, 0x4f, 0x41 }, "ArrowUp")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x42 }, "ArrowDown")]
    [Arguments(new byte[] { 0x1b, 0x4f, 0x42 }, "ArrowDown")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x5a }, "ShiftTab")]
    public async Task Each_wire_key_has_its_terminal_bytes(byte[] bytes, string key) =>
        await Assert.That(SpecialKeyMapper.Map(bytes)).IsEqualTo(key);

    [Test]
    [Arguments(new byte[] { 0x78 })]
    [Arguments(new byte[] { 0x1b, 0x5b })]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x43 })]
    [Arguments(new byte[] { })]
    public async Task Anything_else_is_dropped(byte[] bytes) =>
        await Assert.That(SpecialKeyMapper.Map(bytes)).IsNull();

    [Test]
    public async Task The_choices_cover_the_vocabulary_once_each() {
        await Assert.That(SpecialKeyMapper.Choices.Select(c => c.Key)).IsEquivalentTo(SpecialKeys.All);
        await Assert.That(SpecialKeyMapper.Choices.Select(c => c.Label).Distinct().Count()).IsEqualTo(SpecialKeys.All.Length);
    }
    [Test]
    public async Task A_wheel_report_keeps_its_coordinates_on_the_remote_lane() {
        var report = "\x1b[<65;50;19M"u8.ToArray();
        var key = SpecialKeyMapper.Map(report);
        await Assert.That(key).IsNotNull();
        await Assert.That(TerminalScrollKey.TryDecode(key!, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEquivalentTo(report);
    }

}
