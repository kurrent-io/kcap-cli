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

    sealed class FakeAcpProcess : IAcpProcess {
        public int     Pid            { get; init; } = 4242;
        public bool    HasExited      { get; set; }
        public int?    ExitCode       { get; set; }
        public int     TerminateCalls { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            HasExited = true;
            ExitCode  = 0;
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

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(3);

        await Assert.That(calls[0].Method).IsEqualTo("initialize");

        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[1].Params!.Value.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        await Assert.That(calls[2].Method).IsEqualTo("session/prompt");
        var promptBlocks = calls[2].Params!.Value.GetProperty("prompt");
        await Assert.That(promptBlocks[0].GetProperty("text").GetString()).IsEqualTo("do the thing");
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
    public async Task SendUserInputAsync_after_start_sends_another_session_prompt() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        await h.Runtime.SendUserInputAsync("more").WaitAsync(HangGuard);

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

        var any = false;
        await foreach (var _ in h.Runtime.ReadOutputAsync(h.Cts.Token))
            any = true;

        await Assert.That(any).IsFalse();
    }
}
