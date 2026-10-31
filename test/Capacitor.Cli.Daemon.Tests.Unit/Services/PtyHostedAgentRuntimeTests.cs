using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class PtyHostedAgentRuntimeTests {
    // Records every write so we can assert the split-write ordering.
    sealed class RecordingPty : IPtyProcess {
        public Queue<byte[]> Output { get; } = new();
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
            while (Output.TryDequeue(out var bytes)) yield return bytes;
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
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hello world");

        // Approvals disabled → one bracketed-paste block then the CR spray (one CR per
        // SubmitCarriageReturnSchedule step) so at least one lands past codex's Enter-suppression
        // window (GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~hello world\x1b[201~");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task RequestGracefulStop_writes_exit_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: true);

        await runtime.RequestGracefulStopAsync();

        // "/exit" submits via the same CR spray (a single CR failed to submit it — the
        // SIGTERM-fallback half of GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("/exit");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task SendSpecialKey_translates_via_SpecialKeyMap() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System);

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
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System);

        await runtime.SendSpecialKeyAsync("definitely-not-a-key");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(9)]
    [Arguments(1000)]
    [Arguments(1002)]
    [Arguments(1003)]
    public async Task Wheel_reports_are_dropped_after_the_source_disables_mouse_tracking(int mode) {
        var pty = new RecordingPty();
        await using var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System);
        var report = "\x1b[<64;10;5M"u8.ToArray();
        Capacitor.Remote.Models.TerminalScrollKey.TryCreate(report, out var key);
        await runtime.SendSpecialKeyAsync(key);
        await Assert.That(pty.ByteWrites).IsEmpty();

        pty.Output.Enqueue(System.Text.Encoding.UTF8.GetBytes($"\x1b[?{mode}"));
        pty.Output.Enqueue("h"u8.ToArray());
        await foreach (var _ in runtime.ReadOutputAsync()) { }
        await runtime.SendSpecialKeyAsync(key);
        await Assert.That(pty.ByteWrites.Single()).IsEquivalentTo(report);

        pty.Output.Enqueue(System.Text.Encoding.UTF8.GetBytes($"\x1b[?{mode}l"));
        await foreach (var _ in runtime.ReadOutputAsync()) { }
        await runtime.SendSpecialKeyAsync(key);
        await runtime.SendSpecialKeyAsync("Enter");
        await Assert.That(pty.ByteWrites.Count).IsEqualTo(2);
        await Assert.That(pty.ByteWrites[^1]).IsEquivalentTo(new byte[] { 0x0d });
    }

    [Test]
    public async Task SendRawInput_writes_bytes_verbatim() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System);
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
        var runtime = new PtyHostedAgentRuntime("codex", pty, TimeProvider.System);

        runtime.Resize(100, 30);

        await Assert.That(pty.LastResize).IsEqualTo(((ushort)100, (ushort)30));
    }

    [Test]
    public async Task Vendor_and_pid_are_exposed_from_the_wrapped_pty() {
        var runtime = new PtyHostedAgentRuntime("codex", new RecordingPty(), TimeProvider.System);

        await Assert.That(runtime.Vendor).IsEqualTo("codex");
        await Assert.That(runtime.Pid).IsEqualTo(4321);
    }

    [Test]
    public async Task SubmitAsync_stops_sending_carriage_returns_once_the_process_has_exited() {
        // The ~2.4s submit schedule can outlive the reviewer: HasExited becomes true right after
        // the paste write (before any CR) → the guard must short-circuit, sending zero CRs.
        var pty     = new ClosablePty(exitedAfter: writes => writes >= 1);
        var runtime = new PtyHostedAgentRuntime("codex", pty, TimeProvider.System, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hi");

        await Assert.That(pty.Writes.Count).IsEqualTo(1);                   // just the paste, no CRs
        await Assert.That(pty.Writes[0].StartsWith("\x1b[200~", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SubmitAsync_swallows_a_pipe_closed_write_and_stops() {
        // The race where HasExited hasn't flipped yet but the reviewer's pipe is already closing:
        // the CR write throws a pipe-closed IOException. SendUserInputAsync must NOT propagate it
        // (a benign post-exit write) and must send no further CRs (GitHub #349, Qodo finding #2).
        var pty     = new ClosablePty(throwOnCrIndex: 0);
        var runtime = new PtyHostedAgentRuntime("codex", pty, TimeProvider.System, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hi");   // must complete without throwing

        await Assert.That(pty.Writes.Count).IsEqualTo(2);                   // paste + the CR that threw
        await Assert.That(pty.Writes[1]).IsEqualTo("\r");
    }

    [Test]
    public async Task SendUserInput_sends_a_single_CR_when_approvals_are_enabled() {
        // Interactive session (approvals enabled → a prompt is possible): submit with ONE CR so a
        // stray Enter can't accept a live approval dialog — never the multi-CR spray (Qodo review).
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("codex", pty, TimeProvider.System, approvalsDisabled: false);

        await runtime.SendUserInputAsync("hello");

        await Assert.That(pty.StringWrites.Count).IsEqualTo(2);   // paste + exactly one CR
        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~hello\x1b[201~");
        await Assert.That(pty.StringWrites[1]).IsEqualTo("\r");
    }

    const string ReadyBanner = "\x1b[?2004hbanner";

    static Task PumpAsync(PtyHostedAgentRuntime runtime) =>
        Task.Run(async () => { await foreach (var _ in runtime.ReadOutputAsync()) { } });

    /// <summary>Drains the runtime's output and counts the chunks it has finished reading, so a test
    /// can know a mode change has landed before it moves the clock.</summary>
    sealed class CountingPump {
        int _consumed;

        public CountingPump(PtyHostedAgentRuntime runtime) =>
            _ = Task.Run(async () => {
                await foreach (var _ in runtime.ReadOutputAsync()) Interlocked.Increment(ref _consumed);
            });

        public async Task EmitAsync(RecordingScriptedPtyProcess pty, string text) {
            var target = Volatile.Read(ref _consumed) + 1;
            pty.Emit(text);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref _consumed) < target && DateTime.UtcNow < deadline) await Task.Delay(5);

            if (Volatile.Read(ref _consumed) < target) throw new TimeoutException("The runtime did not read the emitted output.");
        }
    }

    /// <summary>Advances the fake clock by <paramref name="span"/> in slices, yielding between them so
    /// timer continuations run. For a step where nothing is meant to happen.</summary>
    static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan span) {
        for (var advanced = TimeSpan.Zero; advanced < span; advanced += TimeSpan.FromMilliseconds(250)) {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(10);
        }
    }

    /// <summary>Advances the fake clock in slices, yielding between them so timer continuations
    /// run, until <paramref name="done"/> or the span is used up.</summary>
    static async Task StepAsync(FakeTimeProvider time, TimeSpan span, Func<bool> done) {
        var step = TimeSpan.FromMilliseconds(250);

        for (var advanced = TimeSpan.Zero; advanced <= span + span / 2 && !done(); advanced += step) {
            time.Advance(step);
            await Task.Delay(10);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    /// <summary>The launch prompt owns the input lane from the start, so a message the user sends
    /// while the TUI is still starting lands after it, never before.</summary>
    [Test]
    public async Task Launch_prompt_lands_before_a_follow_up_sent_during_startup() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        var followUp = runtime.SendUserInputAsync("and test it");
        _ = PumpAsync(runtime);
        pty.Emit("banner");

        await followUp.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(string.Join("|", pty.Writes))
            .IsEqualTo("\x1b[200~build it\x1b[201~|\r|\x1b[200~and test it\x1b[201~|\r");
        pty.Exit();
    }

    /// <summary>Output that keeps coming, such as a startup spinner, postpones the paste until it stops.</summary>
    [Test]
    public async Task Launch_prompt_waits_until_startup_output_stops() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Emit("spinner");
        await Task.Delay(100);

        time.Advance(TimeSpan.FromMilliseconds(300));
        await Task.Delay(50);
        pty.Emit("spinner");
        await Task.Delay(100);
        time.Advance(TimeSpan.FromMilliseconds(300));
        await Task.Delay(100);

        await Assert.That(pty.Writes).IsEmpty();

        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Writes.Count >= 1);

        await Assert.That(pty.Writes[0]).IsEqualTo("\x1b[200~build it\x1b[201~");
        pty.Exit();
    }

    /// <summary>ConPTY's preamble arrives, then goes quiet while the agent is still booting. Pasting
    /// in that silence leaves the prompt in the composer with its submitting CR lost, so the paste
    /// waits for the agent to enable bracketed paste.</summary>
    [Test]
    public async Task Launch_prompt_waits_for_the_tui_to_enable_bracketed_paste() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false, readyOnBracketedPaste: true);

        runtime.DeliverLaunchPrompt("build it");
        var pump = new CountingPump(runtime);
        await pump.EmitAsync(pty, "\x1b[?9001h\x1b[?1004h");
        await pump.EmitAsync(pty, "\x1b[?25l\x1b[2J\x1b[m\x1b[H\x1b]0;claude\x07\x1b[?25h");

        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow * 4);
        await Task.Delay(100);
        await Assert.That(pty.Writes).IsEmpty();

        await pump.EmitAsync(pty, ReadyBanner);
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow / 2);
        await Task.Delay(100);
        await Assert.That(pty.Writes).IsEmpty();

        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Writes.Count >= 1);

        await Assert.That(pty.Writes[0]).IsEqualTo("\x1b[200~build it\x1b[201~");
        pty.Exit();
    }

    /// <summary>Readiness is the mode as it stands when the quiet window ends: a TUI that turns
    /// bracketed paste off again is not ready, however quiet it has been since turning it on.</summary>
    [Test]
    public async Task Launch_prompt_waits_again_when_the_tui_turns_bracketed_paste_off() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false, readyOnBracketedPaste: true);

        runtime.DeliverLaunchPrompt("build it");
        var pump = new CountingPump(runtime);
        await pump.EmitAsync(pty, ReadyBanner);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(50);
        await pump.EmitAsync(pty, "\x1b[?2004l");

        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow * 4);
        await Task.Delay(100);
        await Assert.That(pty.Writes).IsEmpty();

        await pump.EmitAsync(pty, ReadyBanner);
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow / 2);
        await Task.Delay(100);
        await Assert.That(pty.Writes).IsEmpty();

        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Writes.Count >= 1);

        await Assert.That(pty.Writes[0]).IsEqualTo("\x1b[200~build it\x1b[201~");
        pty.Exit();
    }

    /// <summary>A TUI that never settles still gets its prompt once the startup wait expires.</summary>
    [Test]
    public async Task Launch_prompt_is_pasted_when_the_startup_wait_expires_without_output() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptMaxWait, () => pty.Writes.Count >= 1);

        await Assert.That(pty.Writes[0]).IsEqualTo("\x1b[200~build it\x1b[201~");
        await StepAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay, () => runtime.LaunchPromptDelivery!.IsCompleted);
        await Assert.That(await runtime.LaunchPromptDelivery!).IsEqualTo(LaunchPromptOutcome.DeliveredAfterMaxWait);
        pty.Exit();
    }

    /// <summary>A TUI that has not turned raw input on yet would receive the submit CR as LF, so the
    /// prompt waits for raw input however long startup takes, then for the quiet window after it.</summary>
    [Test]
    public async Task Launch_prompt_waits_for_the_terminal_to_stop_cooking_input() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess { IsInputCooked = true };
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Emit("banner");
        await Task.Delay(100);

        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptMaxWait * 2);
        await Assert.That(pty.Writes).IsEmpty();

        pty.IsInputCooked = false;
        time.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(100);
        await Assert.That(pty.Writes).IsEmpty();

        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow + PtyHostedAgentRuntime.SingleSubmitDelay,
            () => runtime.LaunchPromptDelivery!.IsCompleted);

        await Assert.That(await runtime.LaunchPromptDelivery!).IsEqualTo(LaunchPromptOutcome.Delivered);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\r");
        pty.Exit();
    }

    /// <summary>A startup dialog goes quiet while it waits for an answer. Pasting then would let the
    /// submit Enter pick its default option, so the prompt waits for the dialog however long it
    /// stays, then for the redraw after it to settle.</summary>
    [Test]
    public async Task Launch_prompt_waits_for_a_startup_dialog_to_close() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Emit("dialog");
        await Task.Delay(100);
        runtime.NoteTerminalDialog(true);

        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptMaxWait * 2);
        await Assert.That(pty.Writes).IsEmpty();

        await runtime.SendRawInputAsync("\r"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(2));
        pty.Emit("composer");
        await Task.Delay(100);
        runtime.NoteTerminalDialog(false);

        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow + PtyHostedAgentRuntime.SingleSubmitDelay,
            () => runtime.LaunchPromptDelivery!.IsCompleted);

        await Assert.That(await runtime.LaunchPromptDelivery!).IsEqualTo(LaunchPromptOutcome.Delivered);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\r|\x1b[200~build it\x1b[201~|\r");
        pty.Exit();
    }

    /// <summary>A dialog drawn after the paste holds the submit until it is answered, and the answer
    /// itself is not held behind the waiting submit.</summary>
    [Test]
    public async Task Launch_prompt_submit_waits_for_a_dialog_that_appears_after_the_paste() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Emit("banner");
        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Writes.Count >= 1);
        runtime.NoteTerminalDialog(true);

        await AdvanceAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay * 4);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~");

        await runtime.SendRawInputAsync([0x1b]).WaitAsync(TimeSpan.FromSeconds(2));
        runtime.NoteTerminalDialog(false);

        await StepAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay, () => runtime.LaunchPromptDelivery!.IsCompleted);
        await Assert.That(await runtime.LaunchPromptDelivery!).IsEqualTo(LaunchPromptOutcome.Delivered);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\x1b|\r");
        pty.Exit();
    }

    /// <summary>A dialog that opens while the delivery waits for the input lane, behind a viewer's
    /// keystroke, must not get the paste: the delivery checks again once it holds the lane.</summary>
    [Test]
    public async Task Launch_prompt_is_held_for_a_dialog_that_opens_while_it_waits_for_the_lane() {
        var time    = new FakeTimeProvider();
        var pty     = new GatedRawPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Inner.Emit("banner");
        await Task.Delay(100);

        var key = runtime.SendRawInputAsync([0x1b]);
        await pty.RawWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow * 2);

        runtime.NoteTerminalDialog(true);
        pty.ReleaseRawWrites();
        await key.WaitAsync(TimeSpan.FromSeconds(5));
        await AdvanceAsync(time, PtyHostedAgentRuntime.LaunchPromptMaxWait * 2);
        await Assert.That(string.Join("|", pty.Inner.Writes)).IsEqualTo("\x1b");

        pty.Inner.Emit("composer");
        await Task.Delay(100);
        runtime.NoteTerminalDialog(false);
        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow + PtyHostedAgentRuntime.SingleSubmitDelay,
            () => runtime.LaunchPromptDelivery!.IsCompleted);

        await Assert.That(await runtime.LaunchPromptDelivery!).IsEqualTo(LaunchPromptOutcome.Delivered);
        await Assert.That(string.Join("|", pty.Inner.Writes)).IsEqualTo("\x1b|\x1b[200~build it\x1b[201~|\r");
        pty.Inner.Exit();
    }

    /// <summary>A second dialog can open while the submit waits to retake the lane after the first:
    /// the submit holds for every dialog, not only the one it first saw.</summary>
    [Test]
    public async Task Launch_prompt_submit_waits_for_a_dialog_that_follows_another() {
        var time    = new FakeTimeProvider();
        var pty     = new GatedRawPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Inner.Emit("banner");
        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Inner.Writes.Count >= 1);
        runtime.NoteTerminalDialog(true);
        await AdvanceAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay * 2);

        var answer = runtime.SendRawInputAsync([0x1b]);
        await pty.RawWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.NoteTerminalDialog(false);
        await Task.Delay(100);
        runtime.NoteTerminalDialog(true);
        pty.ReleaseRawWrites();
        await answer.WaitAsync(TimeSpan.FromSeconds(5));

        await AdvanceAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay * 4);
        await Assert.That(string.Join("|", pty.Inner.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\x1b");

        runtime.NoteTerminalDialog(false);
        await StepAsync(time, PtyHostedAgentRuntime.SingleSubmitDelay, () => runtime.LaunchPromptDelivery!.IsCompleted);
        await Assert.That(string.Join("|", pty.Inner.Writes)).IsEqualTo("\x1b[200~build it\x1b[201~|\x1b|\r");
        pty.Inner.Exit();
    }

    /// <summary>An unattended launch submits with a spray of CRs; a dialog that comes up mid-spray holds
    /// the rest of it, so no CR answers the dialog.</summary>
    [Test]
    public async Task An_unattended_launch_holds_its_submit_spray_for_a_dialog() {
        var time    = new FakeTimeProvider();
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, time, approvalsDisabled: true);

        runtime.DeliverLaunchPrompt("review it");
        _ = PumpAsync(runtime);
        pty.Emit("banner");
        await StepAsync(time, PtyHostedAgentRuntime.LaunchPromptQuietWindow, () => pty.Writes.Count >= 1);
        await StepAsync(time, PtyHostedAgentRuntime.SubmitCarriageReturnSchedule[0], () => pty.Writes.Count >= 2);
        runtime.NoteTerminalDialog(true);

        await AdvanceAsync(time, TimeSpan.FromSeconds(3));
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b[200~review it\x1b[201~|\r");

        runtime.NoteTerminalDialog(false);
        await StepAsync(time, TimeSpan.FromSeconds(3), () => runtime.LaunchPromptDelivery!.IsCompleted);
        await Assert.That(pty.Writes.Count(w => w == "\r")).IsEqualTo(PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length);
        pty.Exit();
    }

    /// <summary>Holds raw writes until released, so a test can keep the input lane busy.</summary>
    sealed class GatedRawPty : IPtyProcess {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingScriptedPtyProcess Inner { get; } = new();
        public Task RawWriteEntered => _entered.Task;
        public void ReleaseRawWrites() => _release.TrySetResult();

        public int  Pid       => Inner.Pid;
        public bool HasExited => Inner.HasExited;
        public int? ExitCode  => Inner.ExitCode;

        public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => Inner.ReadOutputAsync(ct);
        public Task WriteAsync(string input) => Inner.WriteAsync(input);

        public async Task WriteAsync(byte[] data) {
            _entered.TrySetResult();
            await _release.Task;
            await Inner.WriteAsync(data);
        }

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Inner.WaitForExitAsync(timeout);
        public Task TerminateAsync(TimeSpan?   timeout = null) => Inner.TerminateAsync(timeout);
        public void Resize(ushort cols, ushort rows) => Inner.Resize(cols, rows);
        public void SendInterrupt() => Inner.SendInterrupt();
    }

    [Test]
    public async Task Launch_prompt_is_dropped_when_a_graceful_stop_is_requested_while_a_dialog_shows() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        _ = PumpAsync(runtime);
        pty.Emit("dialog");
        runtime.NoteTerminalDialog(true);
        await runtime.RequestGracefulStopAsync();
        var outcome = await runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.StopRequested);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("/exit|\r");
        pty.Exit();
    }

    /// <summary>An agent that is already gone gets nothing pasted, and the lane is released for
    /// whoever asks next.</summary>
    [Test]
    public async Task Launch_prompt_is_dropped_when_the_agent_exited_before_startup_settled() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);
        pty.Exit();

        runtime.DeliverLaunchPrompt("build it");
        var outcome = await runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.AgentExited);
        await Assert.That(pty.Writes).IsEmpty();
        await runtime.SendUserInputAsync("later").WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A viewer's keystrokes must reach the TUI while the launch prompt is still waiting for
    /// startup to settle: that is how a human dismisses a dialog the paste would otherwise answer.</summary>
    [Test]
    public async Task Raw_input_during_startup_is_not_held_behind_the_launch_prompt() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        await runtime.SendRawInputAsync([0x1b]).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("\x1b");
        pty.Exit();
    }

    /// <summary>A graceful stop during the startup wait withdraws the prompt: pasting it after the
    /// stop's "/exit" would start a turn the stop just asked to end.</summary>
    [Test]
    public async Task Launch_prompt_is_dropped_when_a_graceful_stop_is_requested_during_startup() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        await runtime.RequestGracefulStopAsync();
        var outcome = await runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.StopRequested);
        await Assert.That(string.Join("|", pty.Writes)).IsEqualTo("/exit|\r");
        pty.Exit();
    }

    /// <summary>Text carrying the paste-end sequence, such as captured terminal output, must not end
    /// the paste early and turn the rest into keystrokes.</summary>
    [Test]
    public async Task An_embedded_paste_end_marker_cannot_end_a_follow_up_paste_early() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        await runtime.SendUserInputAsync("one\x1b[201~\rtwo\x1b[200~three");

        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~one[201~\rtwo[200~three\x1b[201~");
    }

    /// <summary>Dropping only the marker's own ESC would let the ESC before it form a new marker.</summary>
    [Test]
    public async Task An_escape_before_a_paste_end_marker_cannot_form_a_new_marker() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        await runtime.SendUserInputAsync("one\x1b\x1b[201~\rtwo");

        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~one[201~\rtwo\x1b[201~");
    }

    [Test]
    public async Task An_embedded_paste_end_marker_cannot_end_the_launch_prompt_paste_early() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("one\x1b[201~\rtwo");
        _ = PumpAsync(runtime);
        pty.Emit("banner");
        var outcome = await runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.Delivered);
        await Assert.That(pty.Writes[0]).IsEqualTo("\x1b[200~one[201~\rtwo\x1b[201~");
        pty.Exit();
    }

    /// <summary>Disposal during the startup wait abandons the prompt without faulting the delivery.</summary>
    [Test]
    public async Task Launch_prompt_delivery_ends_quietly_when_the_runtime_is_disposed_during_startup() {
        var pty     = new RecordingScriptedPtyProcess();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        runtime.DeliverLaunchPrompt("build it");
        await runtime.DisposeAsync();

        var outcome = await runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.Cancelled);
        await Assert.That(pty.Writes).IsEmpty();
    }

    /// <summary>The graceful stop reaches its "/exit" write while a delivery is parked mid-write —
    /// the state a child that stopped draining its stdin puts every writer in. The parked delivery
    /// holds the input lane until the terminate that follows this stop, so a stop that waited on the
    /// lane unboundedly could never be the thing that got there.</summary>
    [Test]
    public async Task RequestGracefulStop_writes_exit_while_a_parked_delivery_holds_the_lane() {
        var pty     = new ParkingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, TimeProvider.System, approvalsDisabled: false);

        var delivery = runtime.SendUserInputAsync("round 1");
        await pty.FirstWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var bound = PtyHostedAgentRuntime.GracefulStopLaneWait + TimeSpan.FromSeconds(3);
        await runtime.RequestGracefulStopAsync().WaitAsync(bound);

        await Assert.That(pty.Writes).Contains("/exit");
        await Assert.That(delivery.IsCompleted).IsFalse();   // still parked; the lane was never freed

        pty.ReleaseFirstWrite();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Fake PTY whose first write parks until released, modelling a child that has stopped reading
    // its stdin. Later writes go through, so the stop's own "/exit" is observable.
    sealed class ParkingPty : IPtyProcess {
        readonly TaskCompletionSource _entered  = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Lock                 _gate     = new();
        readonly List<string>         _writes   = [];
        int _writeCount;

        public Task FirstWriteEntered => _entered.Task;
        public void ReleaseFirstWrite() => _released.TrySetResult();

        public IReadOnlyList<string> Writes { get { lock (_gate) return [.. _writes]; } }

        public int  Pid       => 77;
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

        public async Task WriteAsync(string input) {
            bool first;

            lock (_gate) {
                _writes.Add(input);
                first = _writeCount++ == 0;
            }

            if (!first) return;

            _entered.TrySetResult();
            await _released.Task;
        }

        public Task WriteAsync(byte[] data) => WriteAsync(System.Text.Encoding.UTF8.GetString(data));
        public void Resize(ushort cols, ushort rows) { }
        public void SendInterrupt() { }
    }

    // Fake PTY that can report HasExited as a function of writes-so-far, and/or throw a
    // pipe-closed IOException on a chosen carriage-return write — to exercise SubmitAsync's
    // mid-schedule exit handling.
    sealed class ClosablePty(Func<int, bool>? exitedAfter = null, int? throwOnCrIndex = null) : IPtyProcess {
        readonly Func<int, bool> _exitedAfter = exitedAfter ?? (_ => false);
        int _crCount;

        public List<string> Writes { get; } = [];

        public int  Pid       => 9;
        public bool HasExited => _exitedAfter(Writes.Count);
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) {
            Writes.Add(input);
            if (input == "\r" && throwOnCrIndex == _crCount++)
                throw new System.IO.IOException("The pipe is being closed.");
            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] data) { Writes.Add(System.Text.Encoding.UTF8.GetString(data)); return Task.CompletedTask; }
        public void Resize(ushort cols, ushort rows) { }
        public void SendInterrupt() { }
    }
}
