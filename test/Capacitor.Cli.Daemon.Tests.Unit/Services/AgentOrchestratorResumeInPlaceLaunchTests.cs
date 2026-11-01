using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// An in-place resume relaunches the ended session natively in its own directory, bound to the original session, and
/// holds the source's lease from arrival until the readiness window has passed. A launch that cannot work is refused
/// before anything starts; one that fails after its agent published is retired before it is reported, once, and
/// nothing in the directory is ever removed.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorResumeInPlaceLaunchTests {
    const string AgentId = "resume-1";
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Dashed  = "01234567-89ab-cdef-0123-456789abcdef";
    const string Token   = "lease-token";

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class LoadableFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string CliPath            => "unused";
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;
        public bool   ResumesIdle        { get; init; }
        public bool   Reopens            { get; init; }
        public int?   ExitWith           { get; init; }
        public bool   ExitsLater         { get; init; }

        public RuntimeStartContext? Context    { get; private set; }
        public FakeAcpRuntime?      AcpRuntime { get; private set; }
        public FakeHostedAgentRuntime? PtyRuntime { get; private set; }
        public int                  Starts     { get; private set; }

        public bool IsAvailable() => true;
        public bool SupportsRestartResume(string? runtimeTransport) => true;
        public bool ResumesIdleWithoutPrompt(string? runtimeTransport) => ResumesIdle;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            Starts++;
            Context = ctx;

            if (Reopens) {
                AcpRuntime = new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, AcpSessionId = ctx.ResumeSessionId!, Reopened = true };

                return Task.FromResult(new HostedRuntimeStart(AcpRuntime, McpConfigPath: null, Transcript: AcpRuntime));
            }

            PtyRuntime = new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true) { ExitCode = ExitWith ?? 0 };
            if (ExitWith is not null) PtyRuntime.ExitGate.SetResult();
            if (ExitsLater) _ = Task.Delay(100, CancellationToken.None).ContinueWith(_ => PtyRuntime.ExitGate.TrySetResult(), TaskScheduler.Default);

            return Task.FromResult(new HostedRuntimeStart(PtyRuntime, McpConfigPath: null, Transcript: null));
        }
    }

    sealed class Fixture : IAsyncDisposable {
        public Fixture(TempDir tmp, LoadableFactory factory, CaptureServerConnection? server = null, LaunchConsentGate? consent = null,
                TimeSpan? readiness = null) {
            Directory.CreateDirectory(tmp.PathTo("checkout"));
            Checkout = tmp.GetResolvedPath("checkout");
            File.WriteAllText(Path.Combine(Checkout, "work.txt"), "uncommitted");
            Store    = AccountDirectory.Normalize(tmp.CreateDir(".claude"));
            Factory  = factory;
            Server   = server ?? new CaptureServerConnection();
            Orch     = AgentOrchestratorHarness.BuildOrchestrator(
                Server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: Checkout, extraRuntimeFactories: [factory], consentGate: consent,
                configure: c => {
                    c.ResumeReadinessWindow = readiness ?? TimeSpan.FromMilliseconds(300);
                    c.Accounts              = new AccountStore(tmp.PathTo("accounts"));
                    c.DefaultDirectories    = new Dictionary<HarnessId, string> { [HarnessId.Claude] = Store };
                });
            Orch.SourceLeaseRenewal = TimeSpan.FromMilliseconds(50);
        }

        public string                  Checkout { get; }
        public string                  Store    { get; }
        public LoadableFactory         Factory  { get; }
        public CaptureServerConnection Server   { get; }
        public AgentOrchestrator       Orch     { get; }

        public LaunchAgentCommand Launch(string? prompt = "carry on", string session = Session, string? store = null, string? directory = null) =>
            new(AgentId: AgentId, Prompt: prompt, Model: "", Effort: null, RepoPath: Checkout, Tools: null, AttachmentIds: null,
                Vendor: Factory.Vendor) {
                ResumeInPlace = new ResumeInPlaceLaunch(session, directory ?? Checkout, Token, null, store ?? Store),
            };

        public string[] Failures() => [.. Server.LaunchFailedCalls.Where(c => c.AgentId == AgentId).Select(c => c.Reason)];

        public async ValueTask DisposeAsync() => await Orch.DisposeAsync();
    }

    [Test]
    public async Task A_resume_runs_in_its_directory_on_the_original_session() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEmpty();
        var ctx = f.Factory.Context!;
        await Assert.That((ctx.ResumeSessionId, ctx.Worktree.Path, ctx.Work, ctx.Prompt)).IsEqualTo((Dashed, f.Checkout, WorkLocation.InPlace, "carry on"));
        await Assert.That(ctx.Account?.Directory).IsEqualTo(f.Store);
        var agent = f.Orch.GetAgentForTest(AgentId)!;
        await Assert.That(agent.SessionId).IsEqualTo(Session);
        await Assert.That(agent.ResumePending).IsFalse();
        var ledgered = f.Orch.ResumeLedger!.TryRead(AgentId)!;
        await Assert.That((ledgered.Launch.Work, ledgered.State, ledgered.VendorSession)).IsEqualTo((WorkLocation.InPlace, ResumeState.Running, Dashed));
    }

    [Test]
    public async Task An_empty_first_message_resumes_idle() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ResumesIdle = true });

        await f.Orch.HandleLaunchAgentForTest(f.Launch(prompt: ""));

        await Assert.That(f.Failures()).IsEmpty();
        await Assert.That(f.Factory.Context!.Prompt).IsEqualTo("");
        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.ActivityClock.AwaitingInput).IsTrue();
    }

    [Test]
    public async Task The_lease_is_renewed_until_readiness_and_ended_after() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), readiness: TimeSpan.FromMilliseconds(400));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Server.HandoffRenews).Contains(Token);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_denied_consent_starts_nothing_and_ends_the_lease() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), consent: AgentOrchestratorHarness.DenyDefaultGate(Tmp.CreateDir("consent")));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures().Single()).StartsWith(LaunchConsentGate.DeniedReasonPrefix);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_refused_acp_bind_retires_the_runtime_and_reports_it() {
        var server = new CaptureServerConnection { AcpEventsAckOverride = _ => new AcpBatchAck(-1, -1, Rejected: true) };
        await using var f = new Fixture(Tmp, new LoadableFactory("cursor") { Reopens = true }, server);

        await f.Orch.HandleLaunchAgentForTest(f.Launch(session: "sess-1", store: AccountDirectory.Normalize(
            Directory.CreateDirectory(f.Orch.HarnessesForTest.Of<Capacitor.Cli.Core.Harness.Cursor.CursorHarness>().Paths.CursorDir).FullName)));

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: acp_binding_unavailable"]);
        await Assert.That(f.Factory.AcpRuntime!.HasExited).IsTrue();
        await Assert.That(f.Factory.AcpRuntime!.ReleaseFirstTurnCalls).IsEqualTo(0);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_runtime_that_exits_at_once_is_reported_and_deletes_nothing() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ExitWith = 3 });

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsNotEmpty();
        await Assert.That(f.Factory.Starts).IsEqualTo(1);
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
    }

    [Test]
    public async Task A_runtime_that_exits_inside_the_readiness_window_is_reported_and_leaves_nothing() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ExitsLater = true }, readiness: TimeSpan.FromSeconds(5));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).Contains($"{ResumeInPlaceRefusals.Failed}: runtime_exited");
        await Assert.That(f.Factory.PtyRuntime!.HasExited).IsTrue();
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_refused_registration_reports_resume_failed() {
        var server = new CaptureServerConnection { RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours") };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), server);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).Contains($"{ResumeInPlaceRefusals.Failed}: registration_refused");
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
    }

    [Test]
    [Arguments("borrowed")]
    [Arguments("resume-session")]
    [Arguments("review")]
    public async Task A_resume_block_on_another_launch_shape_is_refused(string shape) {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));
        var plain = f.Launch();

        await f.Orch.HandleLaunchAgentForTest(shape switch {
            "borrowed"       => plain with { Borrowed = true, BorrowCwd = f.Checkout },
            "resume-session" => plain with { ResumeSessionId = Dashed },
            _                => plain with { Kind = LaunchKind.ReviewFlow },
        });

        await Assert.That(f.Failures().Single()).IsEqualTo(ResumeInPlaceRefusals.Invalid);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_launch_the_probe_would_refuse_is_refused_before_anything_starts() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));

        await f.Orch.HandleLaunchAgentForTest(f.Launch(directory: Path.Combine(f.Checkout, "gone")));

        await Assert.That(f.Failures().Single()).IsEqualTo(ResumeInPlaceRefusals.DirectoryMissing);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
    }

    [Test]
    public async Task A_codex_in_place_resume_is_writable() =>
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.InPlace, isReviewFlow: false, posture: null).Sandbox).IsEqualTo("workspace-write");

    [Test]
    public async Task A_week_old_codex_rollout_is_found_by_its_id() {
        var sessions = Tmp.CreateDir("codex", "sessions");
        var day      = Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "24")).FullName;
        var rollout  = Path.Combine(day, $"rollout-2026-10-24T09-00-00-{Dashed}.jsonl");
        File.WriteAllText(rollout, "{}\n");
        File.WriteAllText(Path.Combine(day, "rollout-2026-10-24T09-00-00-11111111-2222-3333-4444-555555555555.jsonl"), "{}\n");

        await Assert.That(CodexSessionRolloutLocator.TryLocateSession(sessions, Dashed)).IsEqualTo((Session, rollout));
        await Assert.That(CodexSessionRolloutLocator.TryLocateSession(sessions, "99999999-2222-3333-4444-555555555555")).IsNull();
    }
}
