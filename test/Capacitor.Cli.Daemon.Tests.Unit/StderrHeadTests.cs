namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>Pins that <see cref="StderrHead"/> never holds more than its cap, whether the excess
/// arrives as many lines or as one long one, and that it keeps the start, not the end.</summary>
public class StderrHeadTests {
    [Test]
    [Arguments(new[] { "0123456789abcdefghij" }, "0123456789abcde\n")]
    [Arguments(new[] { "aaaa", "bbbb", "cccc", "dddd" }, "aaaa\nbbbb\ncccc\n")]
    [Arguments(new[] { "aaaa", "bbbbbbbbbbbbbbbbbbbb", "cccc" }, "aaaa\nbbbbbbbbbb\n")]
    [Arguments(new[] { "short" }, "short\n")]
    public async Task Keeps_the_start_within_the_cap(string[] lines, string expected) {
        var head = new StderrHead(16);

        foreach (var line in lines) head.Capture(line);

        await Assert.That(head.Text).IsEqualTo(expected);
        await Assert.That(head.Text!.Length).IsLessThanOrEqualTo(16);
    }

    [Test]
    public async Task Is_null_until_something_is_captured() =>
        await Assert.That(new StderrHead(16).Text).IsNull();
}
