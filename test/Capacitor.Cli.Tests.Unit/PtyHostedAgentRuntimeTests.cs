using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class PtyHostedAgentRuntimeTests {
    // Records every write so we can assert the split-write ordering.
    sealed class RecordingPty : IPtyProcess {
        public List<string> StringWrites { get; } = [];
        public List<byte[]>  ByteWrites   { get; } = [];
        public (ushort Cols, ushort Rows)? LastResize { get; private set; }

        public int  Pid       => 4321;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) { StringWrites.Add(input); return Task.CompletedTask; }
        public Task WriteAsync(byte[] data)  { ByteWrites.Add(data);    return Task.CompletedTask; }
        public void Resize(ushort cols, ushort rows) => LastResize = (cols, rows);
        public void SendInterrupt() { }
    }

    [Test]
    public async Task SendUserInput_wraps_text_in_a_bracketed_paste_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.SendUserInputAsync("hello world");

        // the text is delivered as one bracketed-paste block (ESC[200~ … ESC[201~) so the
        // CLI's TUI treats it as a single paste, then submitted with the escalating
        // carriage-return schedule (one CR per SubmitCarriageReturnSchedule step) so at least
        // one CR lands as a distinct keypress after the paste is ingested (GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~hello world\x1b[201~");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task RequestGracefulStop_writes_exit_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.RequestGracefulStopAsync();

        // "/exit" is submitted with the same escalating carriage-return schedule as user input
        // (a single CR failed to submit it — the SIGTERM-fallback half of GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("/exit");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task SendSpecialKey_translates_via_SpecialKeyMap() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        // SpecialKeyMap.ToBytes recognizes PascalCase keys: Escape/Tab/Enter/CtrlC/ArrowUp/
        // ArrowDown/ShiftTab. "Enter" maps to [0x0d].
        await runtime.SendSpecialKeyAsync("Enter");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0].Length).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0][0]).IsEqualTo((byte)0x0d);
    }

    [Test]
    public async Task SendSpecialKey_unknown_key_writes_nothing() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.SendSpecialKeyAsync("definitely-not-a-key");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SendRawInput_writes_bytes_verbatim() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);
        var data    = new byte[] { 1, 2, 3 };

        await runtime.SendRawInputAsync(data);

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0].Length).IsEqualTo(data.Length);
        await Assert.That(pty.ByteWrites[0][0]).IsEqualTo(data[0]);
        await Assert.That(pty.ByteWrites[0][1]).IsEqualTo(data[1]);
        await Assert.That(pty.ByteWrites[0][2]).IsEqualTo(data[2]);
    }

    [Test]
    public async Task Resize_forwards_to_pty() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("codex", pty);

        runtime.Resize(100, 30);

        await Assert.That(pty.LastResize).IsEqualTo(((ushort)100, (ushort)30));
    }

    [Test]
    public async Task Vendor_and_pid_are_exposed_from_the_wrapped_pty() {
        var runtime = new PtyHostedAgentRuntime("codex", new RecordingPty());

        await Assert.That(runtime.Vendor).IsEqualTo("codex");
        await Assert.That(runtime.Pid).IsEqualTo(4321);
    }
}
