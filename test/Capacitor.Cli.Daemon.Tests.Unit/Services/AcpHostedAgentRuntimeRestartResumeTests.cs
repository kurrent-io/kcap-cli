using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A restart resume starts the runtime on <c>session/load</c> of the ledger's session instead of
/// <c>session/new</c>: the replay is dropped, because the server already holds that history, and the
/// nudge is the first turn.
/// </summary>
public class AcpHostedAgentRuntimeRestartResumeTests {
    static readonly TimeSpan HangGuard = WaitHarness.AcpHangGuard;

    const string ResumedSession = "resumed-session-7";

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid       => 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public async Task WaitForExitAsync(TimeSpan? timeout = null) {
            if (timeout is { } t) await Task.WhenAny(_exited.Task, Task.Delay(t));
            else await _exited.Task;
        }

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        readonly List<AcpEventEnvelope> _envelopes = [];
        Task _fakeLoop = Task.CompletedTask;
        Task _drain    = Task.CompletedTask;

        public FakeAcpAgent            Fake    { get; } = new();
        public CancellationTokenSource Cts     { get; } = new();
        public AcpHostedAgentRuntime   Runtime { get; }

        public Harness() =>
            Runtime = new AcpHostedAgentRuntime(
                new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance),
                new FakeAcpProcess(), NullLogger.Instance, TimeProvider.System);

        public Task StartAsync(string? prompt, string? resumeSessionId) {
            _fakeLoop = Fake.RunAsync(Cts.Token);
            _drain    = DrainAsync();

            return Runtime.StartAsync("/abs/worktree", prompt, Cts.Token, resumeSessionId: resumeSessionId).WaitAsync(HangGuard);
        }

        async Task DrainAsync() {
            await foreach (var envelope in Runtime.Envelopes.ReadAllAsync(Cts.Token))
                lock (_envelopes) _envelopes.Add(envelope);
        }

        public IReadOnlyList<AcpEventEnvelope> Envelopes() {
            lock (_envelopes) return _envelopes.ToArray();
        }

        public async ValueTask DisposeAsync() {
            await Cts.CancelAsync();
            try { await _fakeLoop.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
            try { await _drain.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    [Test]
    public async Task A_resume_loads_the_session_drops_its_replay_and_holds_every_turn_until_released() {
        await using var h = new Harness();
        h.Fake.SetSessionLoadReplay([FakeAcpAgent.DefaultAgentMessageChunkUpdate(ResumedSession, "replayed-history")]);
        h.Fake.EnqueuePromptScript(
            [FakeAcpAgent.DefaultAgentMessageChunkUpdate(ResumedSession, "after-the-nudge")],
            JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone());
        h.Fake.EnqueuePromptScript([], JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone());

        await h.StartAsync("the nudge", ResumedSession);
        await h.Runtime.SendUserInputAsync("typed during the hold").WaitAsync(HangGuard);

        await WaitHarness.PollUntilAsync(() => h.Envelopes().Count > 0);
        await Assert.That(h.Runtime.Reopened).IsTrue();
        await Assert.That(h.Envelopes()[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Task.Delay(200);
        await Assert.That(h.Fake.ReceivedCalls.Select(c => c.Method)).DoesNotContain("session/prompt");

        h.Runtime.ReleaseFirstTurn();
        await WaitHarness.PollUntilAsync(() => h.Fake.ReceivedCalls.Count(c => c.Method == "session/prompt") == 2);

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Select(c => c.Method)).DoesNotContain("session/new");

        var load = calls.Single(c => c.Method == "session/load").Params!.Value;
        await Assert.That(load.GetProperty("sessionId").GetString()).IsEqualTo(ResumedSession);
        await Assert.That(load.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        var prompts = calls.Where(c => c.Method == "session/prompt").Select(c => c.Params!.Value).ToArray();
        await Assert.That(prompts.Select(p => p.GetProperty("sessionId").GetString()!)).IsEquivalentTo([ResumedSession, ResumedSession]);
        await Assert.That(prompts.Select(p => p.GetProperty("prompt")[0].GetProperty("text").GetString()!))
            .IsEquivalentTo(["the nudge", "typed during the hold"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await WaitHarness.PollUntilAsync(() => h.Envelopes().Any(e => e.Text?.Contains("after-the-nudge") == true));

        await Assert.That(h.Runtime.SessionId).IsEqualTo(ResumedSession);
        await Assert.That(h.Envelopes().Any(e => e.Text?.Contains("replayed-history") == true)).IsFalse();

        h.Runtime.ReleaseFirstTurn();
        await Assert.That(h.Fake.ReceivedCalls.Count(c => c.Method == "session/prompt")).IsEqualTo(2);
    }

    /// <summary>A turn still queued behind the hold when the runtime is disposed is never sent, so a caller
    /// awaiting its write is told so rather than left waiting.</summary>
    [Test]
    public async Task Turns_still_held_at_dispose_fail_their_write() {
        var h = new Harness();
        await h.StartAsync("the nudge", ResumedSession);

        var first  = h.Runtime.SendUserInputAndWaitForWriteAsync("one");
        var second = h.Runtime.SendUserInputAndWaitForWriteAsync("two");

        await h.DisposeAsync();

        await Assert.That(async () => await first.WaitAsync(HangGuard)).Throws<InvalidOperationException>();
        await Assert.That(async () => await second.WaitAsync(HangGuard)).Throws<InvalidOperationException>();
        await Assert.That(h.Fake.ReceivedCalls.Select(c => c.Method)).DoesNotContain("session/prompt");
    }

    /// <summary>A vendor's command list can arrive while the load's suppression is still on; it is the
    /// session's live state, not replayed transcript, so it is kept.</summary>
    [Test]
    public async Task A_command_list_during_the_load_is_published() {
        await using var h = new Harness();
        IReadOnlyList<HostedAgentCommand>? published = null;
        h.Runtime.OnCommandsAvailable = commands => published = commands;
        h.Fake.SetSessionLoadReplay([
            FakeAcpAgent.BuildSessionUpdateNotification(ResumedSession, FakeAcpAgent.BuildAvailableCommandsUpdate([("review", "Review the diff")]))
        ]);

        await h.StartAsync(null, ResumedSession);

        await WaitHarness.PollUntilAsync(() => published is { Count: > 0 });
        await Assert.That(published!.Select(c => c.Name)).Contains("review");
    }

    [Test]
    public async Task A_refused_load_fails_the_start() {
        await using var h = new Harness();
        h.Fake.FailSessionLoad(-32000, "Session is active in another process");

        var start = h.StartAsync("the nudge", ResumedSession);

        await Assert.That(async () => await start).Throws<AcpHostedAgentRuntime.AcpHandshakeFailedException>();
        await Assert.That(h.Fake.ReceivedCalls.Select(c => c.Method)).DoesNotContain("session/new");
        await Assert.That(h.Fake.ReceivedCalls.Select(c => c.Method)).DoesNotContain("session/prompt");
    }

    [Test]
    public async Task A_resume_against_an_agent_without_loadSession_fails_before_any_session_request() {
        await using var h = new Harness();
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(1, loadSession: false));

        var start = h.StartAsync("the nudge", ResumedSession);

        await Assert.That(async () => await start).Throws<AcpHostedAgentRuntime.AcpHandshakeFailedException>();
        await Assert.That(h.Fake.ReceivedCalls.Select(c => c.Method)).IsEquivalentTo(["initialize"]);
    }
}
