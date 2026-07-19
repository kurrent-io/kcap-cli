// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeTests.cs
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Exercises <see cref="AcpHostedAgentRuntime"/> end-to-end against <see cref="FakeAcpAgent"/> — no
/// real <c>cursor-agent</c> process is spawned; <see cref="FakeAcpProcess"/> stands in for the
/// process-lifecycle side (Task 10's factory implements the real one over
/// <see cref="System.Diagnostics.Process"/>).
/// </summary>
public class AcpHostedAgentRuntimeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="IAcpProcess"/> fake whose <see cref="WaitForExitAsync"/> genuinely blocks (like
    /// the real <c>AcpChildProcess</c> over a live child process) until <see cref="SignalExited"/>
    /// is called or the process is <see cref="TerminateAsync"/>d — needed to exercise Fix
    /// E's "stay open until the process exits" contract on <c>AcpHostedAgentRuntime.ReadOutputAsync</c>.
    /// The un-signalled default (used by every pre-existing test in this file, which never calls
    /// <see cref="SignalExited"/>) matches the OLD <c>Task.CompletedTask</c> behavior closely enough
    /// for handshake/update/stop tests that don't touch <c>ReadOutputAsync</c> at all.
    /// </summary>
    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid            { get; init; } = 4242;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }

        /// <summary>Simulates the child process exiting on its own (no Terminate call).</summary>
        public void SignalExited(int exitCode = 0) {
            HasExited = true;
            ExitCode  = exitCode;
            _exited.TrySetResult();
        }

        public async Task WaitForExitAsync(TimeSpan? timeout = null) {
            if (timeout is { } t) {
                await Task.WhenAny(_exited.Task, Task.Delay(t)).ConfigureAwait(false);
            } else {
                await _exited.Task.ConfigureAwait(false);
            }
        }

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            SignalExited();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    [Test]
    public async Task StartAsync_performs_initialize_then_session_new_then_session_prompt_in_order() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        // Fix E: StartAsync returns once session/new resolves — it fires the initial
        // session/prompt as untracked background work rather than awaiting it, so the fake may not
        // have received (or recorded) it yet at this exact instant. Poll rather than asserting
        // immediately.
        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(3);

        await Assert.That(calls[0].Method).IsEqualTo("initialize");

        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[1].Params!.Value.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        await Assert.That(calls[2].Method).IsEqualTo("session/prompt");
        var promptBlocks = calls[2].Params!.Value.GetProperty("prompt");
        await Assert.That(promptBlocks[0].GetProperty("text").GetString()).IsEqualTo("do the thing");
    }

    // A live capability probe against the real cursor-agent found it performs file/shell operations
    // itself and never requests client fs/terminal, so the daemon must keep advertising NONE of
    // them — advertising a capability we can't safely enforce is exactly the failure mode this
    // locks against. Fails loudly if a future change flips one on without revisiting that decision.
    [Test]
    public async Task StartAsync_advertises_no_fs_or_terminal_client_capabilities() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");

        var clientCapabilities = calls[0].Params!.Value.GetProperty("clientCapabilities");
        await Assert.That(clientCapabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean()).IsFalse();
        await Assert.That(clientCapabilities.GetProperty("fs").GetProperty("writeTextFile").GetBoolean()).IsFalse();
        await Assert.That(clientCapabilities.GetProperty("terminal").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Scripted_agent_message_chunk_update_is_surfaced_as_reduced_DTO() {
        await using var h = new Harness();

        var update = FakeAcpAgent.BuildSessionUpdateNotification(
            FakeAcpAgent.FixedSessionId,
            FakeAcpAgent.BuildAgentMessageChunkUpdate("hello there"));
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { update }, result);

        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.AgentMessageChunk);
        await Assert.That(received.Text).IsEqualTo("hello there");
    }

    [Test]
    public async Task Unknown_sessionUpdate_variant_is_surfaced_as_Unknown_with_Raw_and_does_not_throw() {
        await using var h = new Harness();

        var weirdUpdate = System.Text.Json.JsonDocument.Parse("""{"sessionUpdate":"some_future_variant","foo":"bar"}""").RootElement.Clone();
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, weirdUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.Unknown);
        await Assert.That(received.Raw).IsNotNull();
        await Assert.That(received.Raw!.Value.GetProperty("foo").GetString()).IsEqualTo("bar");
    }

    [Test]
    public async Task SessionInfo_update_is_reduced_to_SessionInfo_with_the_captured_title() {
        await using var h = new Harness();

        var infoUpdate   = FakeAcpAgent.BuildSessionInfoUpdate("Shell Reporter");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, infoUpdate);
        var result       = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.SessionInfo);
        await Assert.That(received.Title).IsEqualTo("Shell Reporter");
    }

    // ── Option B task 1: Reduce() tool-call/tool-result field capture ──────────────

    [Test]
    public async Task ToolCall_update_captures_ToolInputJson_from_rawInput() {
        await using var h = new Harness();

        var toolCall = FakeAcpAgent.BuildToolCallUpdate(
            "call-1", "Run shell command", "execute", "pending",
            rawInputJson: """{"command":"echo hi"}""");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, toolCall);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.ToolCall);
        await Assert.That(received.ToolCallId).IsEqualTo("call-1");
        await Assert.That(received.ToolTitle).IsEqualTo("Run shell command");
        await Assert.That(received.ToolInputJson).IsEqualTo("""{"command":"echo hi"}""");
    }

    [Test]
    public async Task ToolCall_update_without_rawInput_leaves_ToolInputJson_null() {
        await using var h = new Harness();

        var toolCall = FakeAcpAgent.BuildToolCallUpdate("call-1", "Run shell command", "execute", "pending");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, toolCall);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolInputJson).IsNull();
    }

    [Test]
    public async Task Status_only_ToolCallUpdate_captures_status_but_no_ToolResultText() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "in_progress");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.ToolCallUpdate);
        await Assert.That(received.ToolStatus).IsEqualTo("in_progress");
        await Assert.That(received.ToolResultText).IsNull();
        await Assert.That(received.ToolIsError).IsFalse();
    }

    [Test]
    public async Task Terminal_ToolCallUpdate_captures_ToolResultText_from_content_text_block() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "completed", resultText: "hi\n");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolStatus).IsEqualTo("completed");
        await Assert.That(received.ToolResultText).IsEqualTo("hi\n");
        await Assert.That(received.ToolIsError).IsFalse();
    }

    [Test]
    public async Task Terminal_failed_ToolCallUpdate_sets_ToolIsError_true() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "failed", resultText: "boom");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolStatus).IsEqualTo("failed");
        await Assert.That(received.ToolResultText).IsEqualTo("boom");
        await Assert.That(received.ToolIsError).IsTrue();
    }

    [Test]
    public async Task Terminal_ToolCallUpdate_falls_back_to_rawOutput_when_no_content_text_block() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate(
            "call-1", "completed", rawOutputJson: """{"exitCode":0}""");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolResultText).IsEqualTo("""{"exitCode":0}""");
    }

    [Test]
    public async Task SendUserInputAsync_after_start_sends_another_session_prompt() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        // Fix E: SendUserInputAsync fires the session/prompt as untracked background work
        // and returns as soon as it's queued, NOT once the fake has received/answered it — so poll
        // for the call to land instead of asserting immediately after the await returns.
        await h.Runtime.SendUserInputAsync("more").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var promptCalls = h.Fake.ReceivedCalls.Where(c => c.Method == "session/prompt").ToArray();
        await Assert.That(promptCalls.Length).IsEqualTo(1);
        await Assert.That(promptCalls[0].Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString())
            .IsEqualTo("more");
    }

    [Test]
    public async Task RequestGracefulStopAsync_sends_session_cancel_notification() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        await h.Runtime.RequestGracefulStopAsync().WaitAsync(HangGuard);

        // session/cancel is a notification (no id) — give the fake's read loop a moment to record it.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/cancel") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var cancelCall = h.Fake.ReceivedCalls.SingleOrDefault(c => c.Method == "session/cancel");
        await Assert.That(cancelCall.Method).IsEqualTo("session/cancel");
        await Assert.That(cancelCall.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
    }

    [Test]
    public async Task SendRawInputAsync_throws_NotSupportedException_and_ReadOutputAsync_yields_nothing() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Runtime.SendRawInputAsync(new byte[] { 1 }));

        // ReadOutputAsync never yields a byte, but (Fix E) it also must not complete on its
        // own — it stays open until the process exits or ct cancels (see the dedicated
        // ReadOutputAsync_* tests below for that contract). Cancel to end the enumeration here,
        // since this test's focus is "yields nothing", not "when does it end".
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(h.Cts.Token);

        var any = false;
        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(readCts.Token))
                any = true;
        });

        await Task.Delay(50); // give the loop a moment to (not) yield anything
        await readCts.CancelAsync();
        await readTask.WaitAsync(HangGuard);

        await Assert.That(any).IsFalse();
    }

    // ── Fix E: ReadOutputAsync must stay open (not complete immediately) ──────────

    [Test]
    public async Task ReadOutputAsync_stays_open_until_the_process_exits() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(h.Cts.Token)) {
                // never yields
            }
            completed.TrySetResult();
        });

        // Give the enumerator a chance to run — it must NOT have completed yet (the old
        // implementation returned/yield-broke immediately, which is exactly the bug: the
        // orchestrator's read loop would see the output stream "end" for a still-live agent and
        // finalize it as Failed).
        await Task.Delay(200);
        await Assert.That(completed.Task.IsCompleted).IsFalse();

        h.Process.SignalExited();

        await completed.Task.WaitAsync(HangGuard);
        await readTask.WaitAsync(HangGuard);
    }

    [Test]
    public async Task ReadOutputAsync_stays_open_until_the_cancellation_token_fires() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        using var readCts = new CancellationTokenSource();
        var        completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(readCts.Token)) {
                // never yields
            }
            completed.TrySetResult();
        });

        await Task.Delay(200);
        await Assert.That(completed.Task.IsCompleted).IsFalse();

        await readCts.CancelAsync();

        await completed.Task.WaitAsync(HangGuard);
        await readTask.WaitAsync(HangGuard);
    }

    // ── Fix E: StartAsync/SendUserInputAsync must not block on turn completion ───────

    [Test]
    public async Task StartAsync_does_not_await_the_initial_prompt_turn_to_completion() {
        await using var h = new Harness();

        // The fake answers initialize/session-new immediately but holds EVERY session/prompt
        // response indefinitely — models a long-running first turn. Before the fix,
        // AcpHostedAgentRuntime.StartAsync awaited SendPromptAsync (the initial prompt) to
        // completion, so this would hang past HangGuard; the fix fires it as untracked background
        // work and returns once session/new resolves.
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        // Prove the prompt really was sent (as background work) even though we never released the
        // hold — the fake recorded the call as soon as it arrived, before answering.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsTrue();

        // Release the held response so the harness can tear down cleanly.
        h.Fake.HoldPromptResponses.TrySetResult();
    }

    [Test]
    public async Task SendUserInputAsync_returns_promptly_without_waiting_for_the_prompt_turn_to_complete() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        // Establish the session with no initial prompt (StartAsync's own prompt firing is covered
        // by the test above), then hold every subsequent session/prompt response.
        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Before the fix, SendUserInputAsync awaited SendPromptAsync (the session/prompt round
        // trip) to completion — with the response held indefinitely, this call would hang past
        // HangGuard. The fix fires the prompt as untracked background work and returns immediately.
        await h.Runtime.SendUserInputAsync("more").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count(c => c.Method == "session/prompt") < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsTrue();

        h.Fake.HoldPromptResponses.TrySetResult();
    }
}
