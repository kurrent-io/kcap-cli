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
    public async Task SendUserInput_writes_text_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.SendUserInputAsync("hello world");

        await Assert.That(pty.StringWrites).IsEquivalentTo(new List<string> { "hello world", "\r" });
    }

    [Test]
    public async Task RequestGracefulStop_writes_exit_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.RequestGracefulStopAsync();

        await Assert.That(pty.StringWrites).IsEquivalentTo(new List<string> { "/exit", "\r" });
    }

    [Test]
    public async Task SendSpecialKey_translates_via_SpecialKeyMap() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        // SpecialKeyMap.ToBytes recognizes PascalCase keys: Escape/Tab/Enter/CtrlC/ArrowUp/
        // ArrowDown/ShiftTab. "Enter" maps to [0x0d].
        await runtime.SendSpecialKeyAsync("Enter");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0]).IsEquivalentTo(new byte[] { 0x0d });
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
        await Assert.That(pty.ByteWrites[0]).IsEquivalentTo(data);
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
