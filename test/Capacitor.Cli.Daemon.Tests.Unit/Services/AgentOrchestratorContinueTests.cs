using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The kcap continue fallback: the interrupted agent and session end as superseded, a fresh agent
/// starts under a new id in the same worktree, and its session takes over the old one's work.
/// </summary>
public class AgentOrchestratorContinueTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string OldAgent   = "old-agent-1";
    const string OldSession = "session-old-1";
    const string NewSession = "acp-new-1";

    sealed class ContinueFactory(CaptureServerConnection server) : IHostedAgentRuntimeFactory {
        public string CliPath => "unused";
        public string Vendor => "cursor";
        public bool SupportsUnattended => false;
        public bool IsAvailable() => true;
        public bool ResumeSupported { get; init; }
        public bool SupportsRestartResume(string? runtimeTransport) => ResumeSupported;

        public ConcurrentQueue<(RuntimeStartContext Ctx, int ReportsBefore)> Starts { get; } = new();

        public bool Fails { get; init; }

        /// <summary>A terminal runtime: its session id is learned by discovery, not at publish.</summary>
        public bool PtyStyle { get; init; }

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            int reports;
            lock (server.ShutdownReports) reports = server.ShutdownReports.Count;
            Starts.Enqueue((ctx, reports));

            if (Fails) throw new InvalidOperationException("the agent exited before its handshake");

            if (PtyStyle)
                return Task.FromResult(new HostedRuntimeStart(new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true), McpConfigPath: null, Transcript: null));

            var runtime = new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, AcpSessionId = NewSession };

            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
        }
    }

    sealed record Fixture(AgentOrchestrator Orch, CaptureServerConnection Server, ContinueFactory Factory, DaemonConfig Config, string Repo);

    Fixture Build(bool resumeSupported = false, bool fails = false, bool ptyStyle = false) {
        var repo   = Tmp.CreateDir("repo");
        var server = new CaptureServerConnection();
        var factory = new ContinueFactory(server) { ResumeSupported = resumeSupported, Fails = fails, PtyStyle = ptyStyle };
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repo, extraRuntimeFactories: [factory],
            configure: c => {
                captured = c;
                c.InstanceId = "instance-now";
                c.ServerUrl = _server.Url!;
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
            });

        // A loaded suite can hold a stub's answer past the production budget.
        orch.ContinueSummaryBudget = TimeSpan.FromMinutes(2);

        return new Fixture(orch, server, factory, captured!, repo);
    }

    ResumeLedgerEntry Entry(Fixture f, bool isPrivate = false) {
        var stamp  = TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "auto", Effort: null, RepoPath: f.Repo,
            WorktreePath: Tmp.CreateDir("wt-old"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "the thing", IsPrivate: isPrivate, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: "capacitor/custom-old", WorktreeIsStandalone: false, WorktreeSourceRepo: f.Repo);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, OldAgent, "daemon-1", stamp, stamp, "cursor", "acp",
            OldSession, VendorSession: null, launch, ResumeState.Resuming, Attempts: 0, LastError: null,
            PredecessorInstanceId: "instance-old", ResumingAt: stamp, DaemonInstanceId: null);
        f.Orch.ResumeLedger!.Write(entry);

        return entry;
    }

    void ServeOldSession() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{OldSession}/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($$"""{"session_id":"{{OldSession}}","status":"ended","title":"Wire the widget","pr_number":7,"pr_title":"Widget wiring"}"""));
        _server.Given(Request.Create().WithPath($"/api/work-items/session/{OldSession}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""[{"work_item_id":"w1","label":"#1 — One","is_primary":true}]"""));
        _server.Given(Request.Create().WithPath($"/api/sessions/{OldSession}/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        _server.Given(Request.Create().WithPath("/api/work-items/declare").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/api/loose-ends/adopt").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"results":[]}"""));
    }

    List<JsonNode> Declares() => [
        .. _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/api/work-items/declare")
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)
    ];

    [Test]
    public async Task Continue_launches_a_new_agent_id_then_ends_the_old_pair_as_superseded() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f);

        var outcome = await f.Orch.ContinueEntryAsync(entry, "native_resume_unsupported", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ResumeOutcome.Continued);
        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .IsEquivalentTo(new[] { (OldAgent, "Completed", "superseded") });

        var (ctx, reportsBefore) = f.Factory.Starts.Single();
        await Assert.That(reportsBefore).IsEqualTo(0);
        await Assert.That(ctx.AgentId).IsNotEqualTo(OldAgent);
        await Assert.That(ctx.ResumeSessionId).IsNull();
        await Assert.That(ctx.Worktree.Path).IsEqualTo(entry.Launch.WorktreePath);
        await Assert.That(ctx.Prompt).StartsWith("build the thing");
        await Assert.That(ctx.Prompt).Contains($"This session continues {OldSession}, which was interrupted (native_resume_unsupported).");
        await Assert.That(ctx.Prompt).EndsWith(ResumeNudge.Build(entry.ResumingAt!.Value, "build the thing"));

        await Assert.That(f.Orch.GetAgentForTest(ctx.AgentId)).IsNotNull();
        await Assert.That(f.Orch.ResumeLedger!.TryRead(OldAgent)).IsNull();
        await Assert.That(f.Orch.ResumeLedger!.TryRead(ctx.AgentId)!.State).IsEqualTo(ResumeState.Running);

        var consentLog = await File.ReadAllTextAsync(
            Path.Combine(f.Config.Store.StateDirectory(f.Config.Name), "consent-decisions.jsonl"));
        await Assert.That(consentLog).Contains($"\"agent_id\":\"{ctx.AgentId}\"");
        await Assert.That(consentLog).Contains("\"source\":\"continue\"");
    }

    [Test]
    public async Task Continue_attaches_the_new_session_to_the_old_work_items() {
        ServeOldSession();
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f);

        await f.Orch.ContinueEntryAsync(entry, "runtime_exited", CancellationToken.None);

        await Assert.That(f.Factory.Starts.Single().Ctx.Prompt).Contains("Wire the widget");

        await WaitHarness.PollUntilAsync(() => Declares().Count > 0);
        var declare = Declares().Single();
        await Assert.That(declare["session_id"]!.GetValue<string>()).IsEqualTo(NewSession);
        await Assert.That(declare["work_item_id"]!.GetValue<string>()).IsEqualTo("w1");
    }

    [Test]
    public async Task Continue_is_used_when_the_vendor_declares_no_resume_support() {
        var f = Build(resumeSupported: false);
        await using var _ = f.Orch;
        Entry(f);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var ctx = f.Factory.Starts.Single().Ctx;
        await Assert.That(ctx.AgentId).IsNotEqualTo(OldAgent);
        await Assert.That(ctx.Prompt).Contains("(native_resume_unsupported)");
        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .IsEquivalentTo(new[] { (OldAgent, "Completed", "superseded") });
        await Assert.That(f.Orch.ResumeLedger!.TryRead(OldAgent)).IsNull();
    }

    [Test]
    public async Task A_private_entry_skips_the_superseded_report_but_still_runs_the_takeover() {
        ServeOldSession();
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f, isPrivate: true);

        var outcome = await f.Orch.ContinueEntryAsync(entry, "native_resume_unsupported", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ResumeOutcome.Continued);
        await Assert.That(f.Server.ShutdownReports).IsEmpty();
        await Assert.That(f.Orch.GetAgentForTest(f.Factory.Starts.Single().Ctx.AgentId)!.IsPrivate).IsTrue();
        await WaitHarness.PollUntilAsync(() => Declares().Count > 0);
        await Assert.That(Declares().Single()["session_id"]!.GetValue<string>()).IsEqualTo(NewSession);
    }

    [Test]
    public async Task A_failed_continue_ends_the_old_agent_once_as_a_failed_resume() {
        var f = Build(fails: true);
        await using var _ = f.Orch;
        Entry(f);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Starts.Count).IsEqualTo(1);
        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .IsEquivalentTo(new[] { (OldAgent, "Failed", "resume_failed:native_resume_unsupported") });
        await Assert.That(f.Orch.ResumeLedger!.TryRead(OldAgent)!.State).IsEqualTo(ResumeState.Interrupted);
    }

    /// <summary>The successor's session id arrives by discovery well after the readiness window, and
    /// the takeover still runs once it does.</summary>
    [Test]
    public async Task A_session_learned_after_the_readiness_window_still_takes_over_the_old_work() {
        ServeOldSession();
        var f = Build(ptyStyle: true);
        await using var _ = f.Orch;
        var entry = Entry(f);

        var outcome = await f.Orch.ContinueEntryAsync(entry, "native_resume_unsupported", CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ResumeOutcome.Continued);

        var agent = f.Orch.GetAgentForTest(f.Factory.Starts.Single().Ctx.AgentId)!;
        await Assert.That(agent.SessionId).IsNull();
        await Task.Delay(f.Config.ResumeReadinessWindow * 3);
        await Assert.That(Declares()).IsEmpty();

        await f.Orch.RunDiscoveryForTest(agent, _ => (NewSession, Tmp.PathTo("transcript.jsonl")));

        await WaitHarness.PollUntilAsync(() => Declares().Count > 0);
        await Assert.That(Declares().Single()["session_id"]!.GetValue<string>()).IsEqualTo(NewSession);
    }
}
