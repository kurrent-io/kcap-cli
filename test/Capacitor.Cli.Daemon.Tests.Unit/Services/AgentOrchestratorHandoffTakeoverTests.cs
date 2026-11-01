using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A hand-off agent takes over its ended source under the server's lease once its own session is
/// known, and the itemised outcome is kept by its session id, whatever becomes of the agent.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorHandoffTakeoverTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string AgentId       = "handoff-1";
    const string Token         = "lease-token-1";
    const string Source        = "0f0f0f0f-1111-4111-8111-000000000001";
    const string SourceWire    = "0f0f0f0f111141118111000000000001";
    const string Successor     = "0f0f0f0f-2222-4222-8222-000000000002";
    const string SuccessorWire = "0f0f0f0f222242228222000000000002";
    const string Declare       = "/api/work-items/declare";

    static readonly HandoffSourceContext SourceContext = new(
        Source, "Wire the widget", "User One", "acme", "widgets", "feature", null, null, "build it", "now test it");

    static HandoffTakeoverAdmission Admitted => new(Token, null, 60);

    sealed class TakeoverFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "unused";
        public string Vendor => "cursor";
        public bool SupportsUnattended => false;
        public bool IsAvailable() => true;
        public bool SupportsRestartResume(string? runtimeTransport) => true;

        /// <summary>A terminal runtime: its session id is learned by discovery, not at publish.</summary>
        public bool PtyStyle { get; init; }

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            if (PtyStyle)
                return Task.FromResult(new HostedRuntimeStart(new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true), McpConfigPath: null, Transcript: null));

            var runtime = new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, AcpSessionId = Successor };

            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
        }
    }

    sealed class Fixture : IAsyncDisposable {
        public required GitRepo                 Repo    { get; init; }
        public required CaptureServerConnection Server  { get; init; }
        public required AgentOrchestrator       Orch    { get; init; }
        public required FakeTimeProvider        Time    { get; init; }
        public required DaemonConfig            Config  { get; init; }

        public async ValueTask DisposeAsync() {
            await Orch.DisposeAsync();
            Repo.Dispose();
        }
    }

    /// <summary><paramref name="beforeStart"/> sees the config before the orchestrator exists, as a file left by an
    /// earlier daemon would be there before this one starts.</summary>
    Fixture Build(
            bool ptyStyle = false, HandoffTakeoverAdmission? admission = null, bool oldServer = false, Action<DaemonConfig>? beforeStart = null) {
        var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("https://github.com/acme/widgets.git");
        var server = new CaptureServerConnection();
        if (!oldServer) server.BeginHandoff = (_, _) => admission ?? Admitted;
        var time = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repo.Path, extraRuntimeFactories: [new TakeoverFactory { PtyStyle = ptyStyle }],
            configure: c => {
                captured    = c;
                c.ServerUrl = _server.Url!;
                beforeStart?.Invoke(c);
            },
            timeProvider: time);

        return new Fixture { Repo = repo, Server = server, Orch = orch, Time = time, Config = captured! };
    }

    static LaunchAgentCommand Launch(GitRepo repo, string agentId = AgentId, bool detached = false) =>
        new(AgentId: agentId, Prompt: "Finish it.", Model: "", Effort: null, RepoPath: repo.Path, Tools: null, AttachmentIds: null, Vendor: "cursor") {
            Handoff = new HandoffLaunch(
                detached
                    ? new HandoffLaunchStart(Source, repo.Head, "Detached", "feature", null, "Branch", "CheckedOutElsewhere", null, false, null, null)
                    : new HandoffLaunchStart(Source, repo.Head, "CreateBranch", "feature", null, "Branch", "None", null, false, null, null),
                SourceContext, SessionCommits: [])
        };

    /// <summary>The ended source with one work item per label and a successor holding <paramref name="held"/>; a
    /// declare is answered by <paramref name="declare"/> when given, else 200.</summary>
    void ServeSource(int workItems = 1, Func<int, JsonObject, WireMock.ResponseMessage?>? declare = null, string held = "[]") {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SourceWire}/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($$"""{"session_id":"{{SourceWire}}","status":"ended","title":"Wire the widget"}"""));
        var items = new JsonArray();
        for (var i = 1; i <= workItems; i++) items.Add(new JsonObject { ["work_item_id"] = $"w{i}", ["label"] = $"#{i} — Item {i}" });
        _server.Given(Request.Create().WithPath($"/api/work-items/session/{SourceWire}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(items.ToJsonString()));
        _server.Given(Request.Create().WithPath($"/api/sessions/{SourceWire}/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        _server.Given(Request.Create().WithPath($"/api/work-items/session/{SuccessorWire}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(held));

        _server.Given(Request.Create().WithPath(Declare).UsingPost())
            .RespondWith(Response.Create().WithCallback(request => {
                var n = Interlocked.Increment(ref _declares);

                return declare?.Invoke(n, JsonNode.Parse(request.Body!)!.AsObject()) ?? Reply(200, "{}");
            }));
        _server.Given(Request.Create().WithPath("/api/loose-ends/adopt").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"results":[]}"""));
    }

    static WireMock.ResponseMessage Reply(int status, string body) => new() {
        StatusCode = status,
        BodyData   = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = body }
    };

    int _declares;

    /// <summary>Declares that reached the server, including one it is still answering.</summary>
    int Declares() => Volatile.Read(ref _declares);

    IEnumerable<WireMock.Logging.ILogEntry> Writes() => _server.LogEntries.Where(e => e.RequestMessage.Method == "POST");

    static async Task<HandoffOutcomeDto?> QueryAsync(AgentOrchestrator orch, string sessionId) {
        using var stream = new MemoryStream();
        await orch.HandleLocalHandoffOutcomeQueryAsync(sessionId, stream, CancellationToken.None);
        stream.Position = 0;
        var frame = (await FrameCodec.ReadAsync(stream, CancellationToken.None))!;
        await Assert.That(frame.Type).IsEqualTo(FrameType.HandoffOutcomeReply);

        return JsonSerializer.Deserialize(frame.Text, HandoffIpcJsonContext.Default.HandoffOutcomeReplyDto)!.Outcome;
    }

    static async Task<HandoffOutcomeDto> SettledAsync(Fixture f, TimeSpan? step = null) {
        HandoffOutcomeDto? outcome = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline) {
            outcome = await QueryAsync(f.Orch, Successor);
            if (outcome is { State: not "running" }) return outcome;
            if (step is { } advance) f.Time.Advance(advance);
            await Task.Delay(20);
        }

        throw new TimeoutException($"the takeover never settled: {outcome?.State ?? "none"}");
    }

    static async Task PumpUntilAsync(Fixture f, Func<bool> done, TimeSpan step) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done() && DateTime.UtcNow < deadline) {
            f.Time.Advance(step);
            await Task.Delay(20);
        }

        await Assert.That(done()).IsTrue();
    }

    static async Task AssertStillRunningAsync(Fixture f) {
        var agent = f.Orch.GetAgentForTest(AgentId);
        await Assert.That(agent).IsNotNull();
        await Assert.That(agent!.Status).IsNotEqualTo("Failed");
        await Assert.That(f.Server.LaunchFailedCalls).IsEmpty();
    }

    [Test]
    public async Task The_takeover_runs_once_the_session_is_known_and_stores_its_outcome() {
        ServeSource();
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(outcome.SourceSessionId).IsEqualTo(Source);
        await Assert.That(outcome.Items.Select(i => (i.Kind, i.Label, i.Result)))
            .IsEquivalentTo([("work_item", "#1 — Item 1", "carried")]);
        await Assert.That(f.Server.HandoffBegins.Select(b => (b.Source, b.Successor))).IsEquivalentTo([(SourceWire, SuccessorWire)]);
        await Assert.That(Declares()).IsEqualTo(1);
        await AssertStillRunningAsync(f);
    }

    /// <summary>A terminal agent's session id arrives by discovery after readiness; a second discovery of the
    /// same session does not start another takeover.</summary>
    [Test]
    public async Task A_session_learned_after_readiness_still_takes_over_once() {
        ServeSource();
        await using var f = Build(ptyStyle: true);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var agent = f.Orch.GetAgentForTest(AgentId)!;
        await Assert.That(agent.SessionId).IsNull();
        await Assert.That(f.Server.HandoffBegins).IsEmpty();
        await Assert.That(await QueryAsync(f.Orch, Successor)).IsNull();

        await f.Orch.RunDiscoveryForTest(agent, _ => (Successor, Tmp.PathTo("transcript.jsonl")));
        await f.Orch.RunDiscoveryForTest(agent, _ => (Successor, Tmp.PathTo("transcript.jsonl")));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Count).IsEqualTo(1);
        await Assert.That(Declares()).IsEqualTo(1);
    }

    /// <summary>The server learns the successor's session only from the session-bearing status the daemon sends,
    /// which can trail the daemon learning it.</summary>
    [Test]
    public async Task A_successor_the_server_does_not_know_yet_is_retried() {
        ServeSource();
        await using var f = Build();
        var known = false;
        f.Server.BeginHandoff = (_, _) => Volatile.Read(ref known) ? Admitted : new HandoffTakeoverAdmission(null, "successor_unknown", 0);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffBegins.IsEmpty);
        await Assert.That((await QueryAsync(f.Orch, Successor))!.State).IsEqualTo("running");

        await WaitHarness.PollUntilAsync(() => {
            lock (f.Server.StatusChangedWithSession) return f.Server.StatusChangedWithSession.Any(s => s.SessionId == SuccessorWire);
        });
        Volatile.Write(ref known, true);
        var outcome = await SettledAsync(f, step: TimeSpan.FromSeconds(2));

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Count(b => b.Admission?.Token is not null)).IsEqualTo(1);
        await Assert.That(Declares()).IsEqualTo(1);
    }

    [Test]
    public async Task A_successor_never_known_is_refused_after_the_window() {
        ServeSource();
        await using var f = Build(admission: new HandoffTakeoverAdmission(null, "successor_unknown", 0));
        var started = f.Time.GetUtcNow();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f, step: TimeSpan.FromSeconds(2));

        await Assert.That(outcome.State).IsEqualTo("refused");
        await Assert.That(outcome.Detail).IsEqualTo("successor_unknown");
        await Assert.That(f.Time.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(58));
        await Assert.That(f.Server.HandoffBegins.Count).IsGreaterThan(1);
        await Assert.That(f.Server.HandoffBegins.Count).IsLessThanOrEqualTo(31);
        await Assert.That(Writes()).IsEmpty();
        await AssertStillRunningAsync(f);
    }

    [Test]
    public async Task A_refused_begin_stores_refused_and_writes_nothing() {
        ServeSource();
        await using var f = Build(admission: new HandoffTakeoverAdmission(null, "source_active", 0));

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("refused");
        await Assert.That(outcome.Detail).IsEqualTo("source_active");
        await Assert.That(f.Server.HandoffBegins.Count).IsEqualTo(1);
        await Assert.That(Writes()).IsEmpty();
        await Assert.That(f.Server.HandoffEnds).IsEmpty();
        await AssertStillRunningAsync(f);
    }

    [Test]
    public async Task An_old_server_without_the_method_stores_failed() {
        ServeSource();
        await using var f = Build(oldServer: true);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("failed");
        await Assert.That(outcome.Detail).IsEqualTo("server_unsupported");
        await Assert.That(Writes()).IsEmpty();
        await AssertStillRunningAsync(f);
    }

    /// <summary>A server that relayed the hand-off has the method, so a begin that fails is a dropped hub, not
    /// an old server.</summary>
    [Test]
    public async Task A_begin_that_fails_is_retried_until_admitted() {
        ServeSource();
        await using var f = Build();
        var calls = 0;
        f.Server.BeginHandoff = (_, _) => Interlocked.Increment(ref calls) <= 2 ? throw new InvalidOperationException("reconnecting") : Admitted;

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f, step: TimeSpan.FromSeconds(2));

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Count).IsEqualTo(3);
        await Assert.That(Declares()).IsEqualTo(1);
    }

    [Test]
    public async Task A_begin_that_never_reaches_the_server_fails_as_unreachable_after_the_window() {
        ServeSource();
        await using var f = Build();
        f.Server.BeginHandoff = (_, _) => throw new InvalidOperationException("reconnecting");
        var started = f.Time.GetUtcNow();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f, step: TimeSpan.FromSeconds(2));

        await Assert.That(outcome.State).IsEqualTo("failed");
        await Assert.That(outcome.Detail).IsEqualTo("server_unreachable");
        await Assert.That(f.Time.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(58));
        await Assert.That(Writes()).IsEmpty();
        await AssertStillRunningAsync(f);
    }

    [Test]
    public async Task Every_write_carries_the_lease_token() {
        ServeSource(workItems: 2);
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await SettledAsync(f);

        var writes = Writes().ToList();
        await Assert.That(writes.Count).IsEqualTo(3);
        foreach (var write in writes)
            await Assert.That(write.RequestMessage.Headers!["X-Kcap-Source-Reservation"].Single()).IsEqualTo(Token);
    }

    [Test]
    public async Task The_lease_is_renewed_while_writing_and_ended_after() {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeSource(declare: (_, _) => {
            arrived.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.WaitBoundedAsync(arrived.Task, "the first write");
        await PumpUntilAsync(f, () => !f.Server.HandoffRenews.IsEmpty, TimeSpan.FromSeconds(5));
        await Assert.That(f.Server.HandoffEnds).IsEmpty();
        release.Set();
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffRenews).Contains(Token);
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffEnds.IsEmpty);
        await Task.Delay(100);
        await Assert.That(f.Server.HandoffEnds).IsEquivalentTo([Token]);
    }

    /// <summary>The advertised lease, not the default interval, bounds when the first renewal goes out.</summary>
    [Test]
    public async Task A_short_lease_is_renewed_before_it_expires() {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeSource(declare: (_, _) => {
            arrived.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build(admission: new HandoffTakeoverAdmission(Token, null, 6));

        try {
            await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
            await WaitHarness.WaitBoundedAsync(arrived.Task, "the first write");
            f.Time.Advance(TimeSpan.FromSeconds(3));

            await WaitHarness.PollUntilAsync(() => !f.Server.HandoffRenews.IsEmpty);
        } finally {
            release.Set();
        }

        await Assert.That((await SettledAsync(f)).State).IsEqualTo("done");
    }

    /// <summary>A renewal that says no, throws or never answers within its timeout all mean the lease may be gone,
    /// which is a lost reservation, not a cancellation.</summary>
    [Test]
    [Arguments("false")]
    [Arguments("throws")]
    [Arguments("hangs")]
    public async Task A_failed_renewal_stops_the_writes(string renewal) {
        using var release = new ManualResetEventSlim();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeSource(workItems: 3, declare: (n, _) => {
            if (n != 2) return null;
            second.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build();
        var failing = false;
        f.Server.RenewHandoff = (_, ct) => !Volatile.Read(ref failing) ? Task.FromResult(true) : renewal switch {
            "false"  => Task.FromResult(false),
            "throws" => Task.FromException<bool>(new TaskCanceledException("the hub is gone")),
            _        => new TaskCompletionSource<bool>().Task
        };

        try {
            await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
            await WaitHarness.WaitBoundedAsync(second.Task, "the second write");
            Volatile.Write(ref failing, true);
            var outcome = await SettledAsync(f, step: TimeSpan.FromSeconds(5));

            await Assert.That(outcome.State).IsEqualTo("failed");
            await Assert.That(outcome.Detail).IsEqualTo(TakeoverResult.Interrupted.ReservationLost);
            await Assert.That(outcome.Items.Select(i => (i.Label, i.Result))).IsEquivalentTo([
                ("#1 — Item 1", "carried"), ("#2 — Item 2", "unknown"), ("#3 — Item 3", "unknown"), ("Loose-end claims", "unknown")
            ]);
            await Assert.That(Declares()).IsEqualTo(2);
            await WaitHarness.PollUntilAsync(() => !f.Server.HandoffEnds.IsEmpty);
            await AssertStillRunningAsync(f);
        } finally {
            release.Set();
        }
    }

    [Test]
    public async Task A_409_reservation_lost_ends_the_run_as_failed() {
        ServeSource(workItems: 3, declare: (n, _) => n == 2 ? Reply(409, """{"code":"source_reservation_lost"}""") : null);
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("failed");
        await Assert.That(outcome.Detail).IsEqualTo(TakeoverResult.Interrupted.ReservationLost);
        await Assert.That(outcome.Items[0].Result).IsEqualTo("carried");
        await Assert.That(outcome.Items[1].Result).IsEqualTo("failed");
        await Assert.That(Declares()).IsEqualTo(2);
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffEnds.IsEmpty);
        await AssertStillRunningAsync(f);
    }

    [Test]
    public async Task The_lease_is_ended_when_the_takeover_throws() {
        ServeSource();
        await using var f = Build();
        f.Orch.RunHandoffTakeoverForTest = (_, _, _, _) => throw new InvalidOperationException("the takeover broke");

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("failed");
        await Assert.That(outcome.Detail).IsEqualTo("takeover_error");
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffEnds.IsEmpty);
        await Assert.That(f.Server.HandoffEnds).IsEquivalentTo([Token]);
        await AssertStillRunningAsync(f);
    }

    /// <summary>The run ends only once shutdown has cancelled it, with <paramref name="result"/>.</summary>
    static void FinishAfterShutdown(Fixture f, Func<TakeoverResult> result) =>
        f.Orch.RunHandoffTakeoverForTest = async (_, _, _, ct) => {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            await Task.Delay(100, CancellationToken.None);

            return result();
        };

    /// <summary>The last write landed before shutdown was signalled, so the work was carried over.</summary>
    [Test]
    public async Task A_takeover_that_finishes_during_shutdown_stores_its_outcome() {
        ServeSource();
        await using var f = Build();
        FinishAfterShutdown(f, () => new TakeoverResult.Completed(new JsonObject(), Unsuccessful: false));

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffBegins.IsEmpty);
        await f.Orch.DisposeAsync();

        await Assert.That((await QueryAsync(f.Orch, Successor))!.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffEnds).IsEquivalentTo([Token]);
    }

    /// <summary>The <c>running</c> left behind is what the next start reads as interrupted.</summary>
    [Test]
    public async Task A_takeover_cut_short_by_shutdown_stores_nothing_and_ends_its_lease() {
        ServeSource();
        await using var f = Build();
        FinishAfterShutdown(f, () => new TakeoverResult.Interrupted(new JsonObject(), TakeoverResult.Interrupted.Cancelled));

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffBegins.IsEmpty);
        await f.Orch.DisposeAsync();

        await Assert.That((await QueryAsync(f.Orch, Successor))!.State).IsEqualTo("running");
        await Assert.That(f.Server.HandoffEnds).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task Shutdown_waits_for_a_takeover_only_so_long() {
        ServeSource();
        await using var f = Build();
        f.Orch.RunHandoffTakeoverForTest = (_, _, _, _) => new TaskCompletionSource<TakeoverResult>().Task;

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.PollUntilAsync(() => !f.Server.HandoffBegins.IsEmpty);
        var started  = f.Time.GetUtcNow();
        var disposed = f.Orch.DisposeAsync().AsTask();
        await PumpUntilAsync(f, () => disposed.IsCompleted, TimeSpan.FromSeconds(1));

        await disposed;
        await Assert.That(f.Time.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    /// <summary>The successor's row becomes a server row once it ends, and its view still asks the daemon.</summary>
    [Test]
    public async Task The_outcome_outlives_the_agent() {
        ServeSource();
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await SettledAsync(f);
        await f.Orch.HandleStopAgentForTest(AgentId);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId) is null);

        var outcome = await QueryAsync(f.Orch, Successor);
        await Assert.That(outcome!.State).IsEqualTo("done");
        await Assert.That(outcome.SourceSessionId).IsEqualTo(Source);
        await Assert.That(outcome.SourceTitle).IsEqualTo("Wire the widget");
    }

    /// <summary>The successor's ledger entry names its source, so a restart before the takeover settles does not
    /// lose it.</summary>
    [Test]
    public async Task A_handoff_launch_records_its_source_in_the_resume_ledger_until_the_takeover_settles() {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeSource(declare: (_, _) => {
            arrived.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build();

        try {
            await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
            await WaitHarness.WaitBoundedAsync(arrived.Task, "the first write");

            await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)!.Launch.HandoffSource).IsEqualTo(SourceContext);
        } finally {
            release.Set();
        }

        await SettledAsync(f);
        await WaitHarness.PollUntilAsync(() => f.Orch.ResumeLedger!.TryRead(AgentId)!.Launch.HandoffSource is null);
    }

    /// <summary>A restart cut the takeover short; the resumed successor finishes it under a fresh lease.</summary>
    [Test]
    public async Task A_recovered_successor_finishes_its_takeover() {
        ServeSource();
        await using var f = Build(beforeStart: c => PutOutcome(c, HandoffOutcomeDto.Running(Source, "Wire the widget")));
        WriteResumableEntry(f, SourceContext);

        await ResumeAsync(f);
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Select(b => (b.Source, b.Successor))).IsEquivalentTo([(SourceWire, SuccessorWire)]);
        await Assert.That(Declares()).IsEqualTo(1);
    }

    /// <summary>A rerun leaves out what the first run attached, so it cannot take primary back from an item the
    /// successor chose since.</summary>
    [Test]
    public async Task A_recovered_successor_does_not_redeclare_what_it_already_holds() {
        ServeSource(workItems: 2, held: """[{"work_item_id":"w1","label":"#1 — Item 1"}]""");
        await using var f = Build(beforeStart: c => PutOutcome(c, HandoffOutcomeDto.Running(Source, "Wire the widget")));
        WriteResumableEntry(f, SourceContext);

        await ResumeAsync(f);
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(Writes().Where(e => e.RequestMessage.Path == Declare)
                .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!["work_item_id"]!.GetValue<string>()))
            .IsEquivalentTo(["w2"]);
        await Assert.That(outcome.Items.Select(i => i.Result)).IsEquivalentTo(["carried", "carried"]);
    }

    /// <summary>A takeover that settled before the restart is not run again.</summary>
    [Test]
    public async Task A_recovered_successor_whose_takeover_settled_takes_over_nothing() {
        ServeSource();
        await using var f = Build(beforeStart: c => PutOutcome(c, HandoffOutcomeDto.Refused(Source, "Wire the widget", "source_active")));
        WriteResumableEntry(f, SourceContext);

        await ResumeAsync(f);
        await Task.Delay(200);

        await Assert.That((await QueryAsync(f.Orch, Successor))!.State).IsEqualTo("refused");
        await Assert.That(f.Server.HandoffBegins).IsEmpty();
    }

    /// <summary>A restart reads the stored <c>running</c> as interrupted, which only the source left in the entry can
    /// finish.</summary>
    [Test]
    public async Task A_takeover_whose_outcome_cannot_be_saved_keeps_its_source() {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeSource(declare: (_, _) => {
            arrived.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build();
        var outcomes = Path.Combine(f.Config.Store.StateDirectory(f.Config.Name), "handoff-outcomes.json");

        try {
            await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
            await WaitHarness.WaitBoundedAsync(arrived.Task, "the first write");
            File.Delete(outcomes);
            Directory.CreateDirectory(outcomes);
        } finally {
            release.Set();
        }

        await Assert.That((await SettledAsync(f)).State).IsEqualTo("done");
        await Task.Delay(200);
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)!.Launch.HandoffSource).IsEqualTo(SourceContext);
    }

    /// <summary>A settled takeover leaves no source in the ledger, so one still there never settled, whether or not
    /// it stored an outcome first.</summary>
    [Test]
    public async Task A_recovered_successor_without_an_outcome_finishes_its_takeover() {
        ServeSource();
        await using var f = Build();
        WriteResumableEntry(f, SourceContext);

        await ResumeAsync(f);
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Select(b => b.Successor)).IsEquivalentTo([SuccessorWire]);
    }

    /// <summary>An entry written without the source has nothing to finish the takeover with.</summary>
    [Test]
    public async Task A_recovered_successor_without_its_source_still_reads_interrupted() {
        ServeSource();
        await using var f = Build(beforeStart: c => PutOutcome(c, HandoffOutcomeDto.Running(Source, "Wire the widget")));
        WriteResumableEntry(f, source: null);

        await ResumeAsync(f);
        await Task.Delay(200);

        await Assert.That((await QueryAsync(f.Orch, Successor))!.State).IsEqualTo("interrupted");
        await Assert.That(f.Server.HandoffBegins).IsEmpty();
    }

    /// <summary>A takeover still running in this process holds its successor against another agent instance of the
    /// same session.</summary>
    [Test]
    public async Task A_second_agent_of_a_successor_with_a_running_takeover_begins_nothing() {
        using var release = new ManualResetEventSlim();
        ServeSource(declare: (_, _) => {
            release.Wait(TimeSpan.FromSeconds(30));

            return null;
        });
        await using var f = Build();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        await WaitHarness.PollUntilAsync(() => Declares() > 0);
        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo, "handoff-2", detached: true));
        await Assert.That(f.Orch.GetAgentForTest("handoff-2")!.SessionId).IsEqualTo(SuccessorWire);
        release.Set();
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("done");
        await Assert.That(f.Server.HandoffBegins.Count).IsEqualTo(1);
        await Assert.That(Declares()).IsEqualTo(1);
    }

    static void PutOutcome(DaemonConfig c, HandoffOutcomeDto outcome) =>
        new HandoffOutcomeStore(c.Store.StateDirectory(c.Name), TimeProvider.System).Put(Successor, outcome);

    static async Task ResumeAsync(Fixture f) {
        f.Orch.LoadResumeLedger();
        var resumed = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await PumpUntilAsync(f, () => resumed.IsCompleted, TimeSpan.FromSeconds(1));
        await resumed;
        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.SessionId).IsEqualTo(SuccessorWire);
    }

    /// <summary>A source this daemon still runs is not ended, whatever the server says, and is refused before the
    /// server records a continuation.</summary>
    [Test]
    public async Task A_source_this_daemon_still_hosts_is_refused_before_begin() {
        ServeSource();
        await using var f = Build();
        var running = AgentOrchestratorHarness.SeedAcpAgent(f.Orch, "source-agent", new FakeAcpRuntime { AcpSessionId = Source });
        running.SessionId = SourceWire;

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo));
        var outcome = await SettledAsync(f);

        await Assert.That(outcome.State).IsEqualTo("refused");
        await Assert.That(outcome.Detail).IsEqualTo("source_active");
        await Assert.That(f.Server.HandoffBegins).IsEmpty();
        await Assert.That(Writes()).IsEmpty();
    }

    void WriteResumableEntry(Fixture f, HandoffSourceContext? source) {
        var stamp    = TimeProvider.System.GetUtcNow();
        var worktree = Tmp.CreateDir("wt-successor");
        var launch = new ResumeLaunchDescriptor(
            Prompt: "Finish it.", Model: "auto", Effort: null, RepoPath: f.Repo.Path,
            WorktreePath: worktree, Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "Hand-off", IsPrivate: false, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: "feature", WorktreeIsStandalone: false, WorktreeSourceRepo: f.Repo.Path, HandoffSource: source);
        f.Orch.ResumeLedger!.Write(new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, AgentId, "daemon-1", stamp, stamp, "cursor", "acp",
            SuccessorWire, Successor, launch, ResumeState.Resuming, Attempts: 0, LastError: null,
            PredecessorInstanceId: "instance-old", ResumingAt: stamp, DaemonInstanceId: null));
    }
}
