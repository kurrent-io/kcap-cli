using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-30: a pasted (large, multi-line) message written to a hosted agent's PTY as raw bytes
/// followed by a carriage return often fails to submit — the CR races the still-ingesting
/// paste and is folded into it as a literal newline, so the text sits in the composer until a
/// later, isolated keystroke finishes it (Codex never submits at all; Claude ~50% of the time).
/// The fix delivers the message as a bracketed paste (ESC[200~ … ESC[201~) so the TUI treats it
/// as one block and the following Enter is an unambiguous submit. This test drives
/// <see cref="AgentOrchestrator.HandleSendInput"/> and asserts the wire shape.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    const string PasteStart = "\x1b[200~";
    const string PasteEnd   = "\x1b[201~";

    [Test]
    public async Task HandleSendInput_wraps_the_message_in_a_bracketed_paste_and_submits_with_a_separate_Enter() {
        const string message = "line-1\nline-2\nline-3\nbig multi-line paste";

        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();

        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance(
            "agent-paste", null, "", null, "/tmp", "codex",
            pty, new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource());
        orch.RegisterAgentForTest(agent);

        await orch.HandleSendInputForTest(new SendInputCommand("agent-paste", message, null));

        // The message is delivered as one bracketed-paste block, then the submitting Enter as a
        // separate write so it lands as a distinct keypress after the paste.
        await Assert.That(pty.Writes.Count).IsEqualTo(2);
        await Assert.That(pty.Writes[0]).IsEqualTo($"{PasteStart}{message}{PasteEnd}");
        await Assert.That(pty.Writes[1]).IsEqualTo("\r");
    }

    /// <summary>PTY double that records the ordered sequence of writes and produces no output.</summary>
    sealed class RecordingPtyProcess : IPtyProcess {
        readonly List<string> _writes = [];
        readonly Lock         _gate   = new();

        public IReadOnlyList<string> Writes {
            get { lock (_gate) { return [.. _writes]; } }
        }

        public int  Pid       => 5150;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) {
            lock (_gate) { _writes.Add(input); }

            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] data) {
            lock (_gate) { _writes.Add(Encoding.UTF8.GetString(data)); }

            return Task.CompletedTask;
        }

        public void Resize(ushort _, ushort __) { }
        public void SendInterrupt() { }
    }
}
