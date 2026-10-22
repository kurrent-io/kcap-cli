using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

public class UnixPtyProcessWriteTests {
    /// <summary>A write that takes only part of the buffer must be continued from where it stopped:
    /// a launch prompt is one write, and a tail that never goes out is a task the agent never sees.</summary>
    [Test]
    public async Task WriteFully_continues_after_a_short_write() {
        var seen = new List<byte>();

        UnixPtyProcess.WriteFully(new byte[] { 1, 2, 3, 4, 5 }, chunk => {
            var n = Math.Min(2, chunk.Length);
            seen.AddRange(chunk.Span[..n].ToArray());
            return n;
        });

        await Assert.That(seen).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public async Task WriteFully_reports_a_failed_write_instead_of_dropping_the_tail() {
        var calls = 0;

        var act = () => UnixPtyProcess.WriteFully(new byte[] { 1, 2, 3 }, _ => { calls++; return -1; });

        await Assert.That(act).Throws<IOException>();
        await Assert.That(calls).IsEqualTo(1);
    }
}
