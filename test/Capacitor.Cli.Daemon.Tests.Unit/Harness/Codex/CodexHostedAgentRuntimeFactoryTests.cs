using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The composite codex factory's routing: a pinned transport first, then the launch class, then the
/// switch the server last acknowledged. The app-server route is exercised through the spawn seam (a
/// fake peer), so no child process runs.
/// </summary>
public class CodexHostedAgentRuntimeFactoryTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────
    sealed class RecordingPtyFactory(string cliPath = "unused-by-this-double") : IHostedAgentRuntimeFactory {
        public string CliPath => cliPath;
        public int StartCalls;
        public string Vendor => "codex";
        public bool IsAvailable() => true;
        public bool SupportsUnattended => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            StartCalls++;
            return Task.FromResult(new HostedRuntimeStart(new StubRuntime(), null));
        }
    }

    sealed class StubRuntime : IHostedAgentRuntime {
        public string Vendor => "codex";
        public int    Pid => 1;
        public bool   HasExited => false;
        public int?   ExitCode => null;
        public bool   EmitsTerminalOutput => true;
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            await Task.CompletedTask; yield break;
        }
        public Task SendUserInputAsync(string text) => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string key) => Task.CompletedTask;
        public Task SendRawInputAsync(byte[] data) => Task.CompletedTask;
        public void Resize(ushort cols, ushort rows) { }
        public Task RequestGracefulStopAsync() => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid => 7;
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" },
            TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [],
        };

    static RuntimeStartContext Ctx(bool isReviewFlow, string worktreePath) => new(
        AgentId: "agent-1", Vendor: "codex", SourceRepoPath: worktreePath,
        Worktree: new WorktreeInfo(Path: worktreePath, Branch: "wt", SourceRepo: worktreePath),
        Prompt: null, Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "https://t.example", DaemonBridgeUrl: null, CapacitorPath: "/opt/kcap");

    static DaemonConfig ConfigWith(bool switchOn, string? version = "0.146.0") {
        var resolution = CodexTransportResolution.Resolve(1, switchOn, () => version);
        return new DaemonConfig {
            CodexResolution             = resolution,
            AcknowledgedCodexResolution = resolution,
            Version                     = "0.146.0",
        };
    }

    CodexHostedAgentRuntimeFactory Factory(
            RecordingPtyFactory pty, bool appServerActive, CodexAppServerSpawnFactory? spawn = null) =>
        new(NewLauncher(), pty, ConfigWith(switchOn: appServerActive), NullLoggerFactory.Instance, TimeProvider.System, spawn);

    /// <summary>A Codex at the factory's CLI path reporting <paramref name="version"/>, for a pinned launch
    /// on a daemon whose switch is off and whose resolution therefore carries no version.</summary>
    string CodexReporting(string version) =>
        Tmp.CreateExecutable("codex", $"#!/bin/sh\necho 'codex-cli {version}'\n");

    // ── Routing decision ─────────────────────────────────────────────────────────────────────
    public enum Kind { Interactive, ReviewFlow, PrReview }

    /// <summary>The installed Codex reports <c>version</c> both to the resolution, when the switch is on,
    /// and at the factory's CLI path, which a pinned launch probes when the switch is off.</summary>
    [Test]
    [Arguments("pty",        "app-server", Kind.Interactive, true,  "0.146.0", false, false)]
    [Arguments("app-server", "pty",        Kind.Interactive, false, "0.146.0", true,  false)]
    [Arguments(null,         "app-server", Kind.ReviewFlow,  false, "0.146.0", true,  false)]
    [Arguments(null,         "app-server", Kind.Interactive, false, "0.146.0", true,  false)]
    [Arguments(null,         "app-server", Kind.Interactive, false, "0.145.9", false, true)]
    [Arguments(null,         "pty",        Kind.ReviewFlow,  true,  "0.146.0", false, false)]
    [Arguments(null,         "app-server", Kind.Interactive, true,  "0.145.9", false, true)]
    [Arguments(null,         null,         Kind.PrReview,    true,  "0.146.0", false, false)]
    [Arguments(null,         null,         Kind.ReviewFlow,  true,  "0.146.0", true,  false)]
    [Arguments(null,         null,         Kind.Interactive, true,  "0.145.9", false, false)]
    [Arguments(null,         null,         Kind.Interactive, false, "0.146.0", false, false)]
    public async Task Route_follows_precedence(
            string? requested, string? expected, Kind kind, bool switchOn, string version,
            bool appServer, bool refused) {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub Codex is a POSIX shell script.");
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(CodexReporting(version)), ConfigWith(switchOn, version),
            NullLoggerFactory.Instance, TimeProvider.System);
        var ctx = Ctx(kind == Kind.ReviewFlow, Home.PathTo("routing")) with {
            IsReview                 = kind == Kind.PrReview,
            RequestedTransport       = requested,
            ExpectedRuntimeTransport = expected,
        };

        var route = factory.Route(ctx);

        await Assert.That(route.AppServer).IsEqualTo(appServer);
        await Assert.That(route.Refusal is not null).IsEqualTo(refused);
        if (refused) await Assert.That(route.Refusal).Contains(version).And.Contains(CodexTransportDecision.VersionFloor);
    }

    /// <summary>A field-absent interactive launch and the advertisement it was dispatched against are
    /// one fact: the server refuses a registration on any other transport.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Field_absent_interactive_routing_matches_the_acknowledged_advertisement(bool active) {
        var config  = ConfigWith(switchOn: active);
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(), config, NullLoggerFactory.Instance, TimeProvider.System);

        var routed = factory.Route(Ctx(isReviewFlow: false, Home.PathTo("interactive-routing")));

        await Assert.That(routed.AppServer ? CodexTransportDecision.AppServer : CodexTransportDecision.Pty)
            .IsEqualTo(config.AcknowledgedCodexResolution.InteractiveTransport);
    }

    /// <summary>Between a commit and the registration that carries it, the server still dispatches
    /// against the older advertisement, so a launch naming no transport follows the acknowledged
    /// resolution whichever way the two differ.</summary>
    [Test]
    [Arguments(Kind.Interactive, true)]
    [Arguments(Kind.Interactive, false)]
    [Arguments(Kind.ReviewFlow,  true)]
    [Arguments(Kind.ReviewFlow,  false)]
    public async Task Field_absent_launch_follows_the_acknowledged_resolution_not_the_committed_one(Kind kind, bool acknowledgedActive) {
        var config = new DaemonConfig {
            CodexResolution             = CodexTransportResolution.Resolve(2, !acknowledgedActive, () => "0.146.0"),
            AcknowledgedCodexResolution = CodexTransportResolution.Resolve(1, acknowledgedActive, () => "0.146.0"),
            Version                     = "0.146.0",
        };
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(), config, NullLoggerFactory.Instance, TimeProvider.System);

        var route = factory.Route(Ctx(kind == Kind.ReviewFlow, Home.PathTo("routing")));

        await Assert.That(route.AppServer).IsEqualTo(acknowledgedActive);
    }

    [Test]
    public async Task A_pinned_app_server_launch_below_the_floor_is_refused_without_starting_pty() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), pty, ConfigWith(switchOn: true, version: "0.145.9"), NullLoggerFactory.Instance, TimeProvider.System);

        await Assert.That(() => factory.StartAsync(
                Ctx(isReviewFlow: true, wt.Path) with { ExpectedRuntimeTransport = "app-server" }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(pty.StartCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Pty_transport_delegates_a_review_flow_to_the_pty_factory() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: false);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None);

        await Assert.That(pty.StartCalls).IsEqualTo(1);
        await Assert.That(start.Runtime).IsTypeOf<StubRuntime>();
    }

    [Test]
    public async Task Resume_on_the_pty_route_starts_the_pty_launcher() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: false);

        await factory.StartAsync(Ctx(isReviewFlow: false, wt.Path) with { ResumeSessionId = "thread-1" }, CancellationToken.None);

        await Assert.That(pty.StartCalls).IsEqualTo(1);
    }

    /// <summary>A resumed local session was a terminal; on a daemon whose interactive launches go to
    /// app-server it must come back as one, or <c>agent attach</c> has nothing to attach to.</summary>
    [Test]
    public async Task A_resume_requesting_pty_starts_the_pty_launcher_on_a_daemon_with_app_server_active() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: true);
        var resumed = Ctx(isReviewFlow: false, wt.Path) with {
            ResumeSessionId = "thread-1", RequestedTransport = "pty", ExpectedRuntimeTransport = "app-server",
        };

        await factory.StartAsync(resumed, CancellationToken.None);

        await Assert.That(pty.StartCalls).IsEqualTo(1);
    }

    /// <summary>A parked reviewer's transcript is envelope-sourced, so a PTY resume would fork it.</summary>
    [Test]
    public async Task Pty_review_flow_resume_is_still_refused() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: false);

        await Assert.That(() => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path) with { ResumeSessionId = "thread-parked-1" }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(pty.StartCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(null)]
    [Arguments("pty")]
    [Arguments("appserver")]
    public async Task Both_transports_support_a_restart_resume(string? transport) {
        await Assert.That(((IHostedAgentRuntimeFactory)Factory(new RecordingPtyFactory(), appServerActive: false)).SupportsRestartResume(transport)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task App_server_resume_still_sends_thread_resume() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        var start = await factory.StartAsync(
            Ctx(isReviewFlow: true, wt.Path) with { ResumeSessionId = "thread-parked-1" }, CancellationToken.None).WaitAsync(HangGuard);
        await start.Runtime.DisposeAsync();

        await Assert.That(fake.ReceivedMethods).Contains("thread/resume");
        await Assert.That(fake.LastResumeThreadId).IsEqualTo("thread-parked-1");
    }

    // ── App-server route (spawn seam; isolated HOME so TrustWorktree touches nothing real) ──────
    [Test]
    [NotInParallel]
    public async Task Active_review_flow_produces_an_app_server_runtime_via_the_spawn_seam() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        var pty = new RecordingPtyFactory();
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        var factory = Factory(pty, appServerActive: true, seam);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(pty.StartCalls).IsEqualTo(0);
        await Assert.That(start.Runtime).IsTypeOf<CodexAppServerHostedAgentRuntime>();
        await Assert.That(start.Runtime.EmitsTerminalOutput).IsFalse();
        await Assert.That(((CodexAppServerHostedAgentRuntime) start.Runtime).ThreadId).IsEqualTo("thread-abc");
        await Assert.That(fake.ReceivedMethods).Contains("thread/start");

        await start.Runtime.DisposeAsync();
    }

    // ── Guard-1 marker (§2.5) ──────────────────────────────────────────────────────────
    [Test]
    public async Task BuildEnv_stamps_the_hosted_appserver_marker_only_when_emitting_envelopes() {
        using var wt = new TempDir();
        var ctx = Ctx(isReviewFlow: true, wt.Path);

        var off = CodexHostedAgentRuntimeFactory.BuildEnv(ctx, emitEnvelopeTranscript: false);
        var on  = CodexHostedAgentRuntimeFactory.BuildEnv(ctx, emitEnvelopeTranscript: true);

        await Assert.That(off.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
        await Assert.That(on["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    [NotInParallel]
    public async Task App_server_launch_is_envelope_sourced_after_activation() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();
        IReadOnlyDictionary<string, string>? capturedEnv = null;

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, env, _, _) => {
            capturedEnv = env;
            return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        };
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(start.Transcript).IsNotNull();
        await Assert.That(start.Runtime.RequiresSourceClaimBeforeFirstTurn).IsTrue();

        await start.Runtime.DisposeAsync();

        await Assert.That(capturedEnv).IsNotNull();
        await Assert.That(capturedEnv!["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    public async Task BuildEnv_marks_a_participant_and_an_interactive_launch_by_kind() {
        var participant = CodexHostedAgentRuntimeFactory.BuildEnv(Ctx(isReviewFlow: true, "/wt") with { FlowRole = "reviewer" }, emitEnvelopeTranscript: true);
        var interactive = CodexHostedAgentRuntimeFactory.BuildEnv(Ctx(isReviewFlow: false, "/wt"), emitEnvelopeTranscript: false);

        await Assert.That(participant["KCAP_SESSION_KIND"]).IsEqualTo("flow_participant");
        await Assert.That(participant["KCAP_FLOW_ROLE"]).IsEqualTo("reviewer");
        await Assert.That(participant.ContainsKey("KCAP_FLOW_AGENT_ID")).IsFalse();
        await Assert.That(interactive["KCAP_SESSION_KIND"]).IsEqualTo("hosted_agent");
        await Assert.That(interactive.ContainsKey("KCAP_FLOW_ROLE")).IsFalse();
    }

    [Test]
    public async Task ApplyChildEnv_drops_an_inherited_flow_role() {
        var childEnv = new Dictionary<string, string?> { ["KCAP_FLOW_ROLE"] = "reviewer" };

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, new Dictionary<string, string> { ["KCAP_SESSION_KIND"] = "hosted_agent" });

        await Assert.That(childEnv.ContainsKey("KCAP_FLOW_ROLE")).IsFalse();
    }

    [Test]
    public async Task ApplyChildEnv_clears_an_inherited_marker_when_the_overlay_does_not_emit() {
        // A daemon whose OWN environment carries the marker must not leak it to a non-emitting child —
        // that child's hook would suppress the only watcher and lose the transcript.
        var childEnv = new Dictionary<string, string?> { ["KCAP_HOSTED_APPSERVER"] = "1", ["PATH"] = "/usr/bin" };
        var overlay  = new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "agent-1" }; // dormant: no marker

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay);

        await Assert.That(childEnv.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
        await Assert.That(childEnv["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(childEnv["PATH"]).IsEqualTo("/usr/bin");
    }

    [Test]
    public async Task ApplyChildEnv_sets_the_marker_when_the_overlay_emits() {
        var childEnv = new Dictionary<string, string?>();
        var overlay  = new Dictionary<string, string> { ["KCAP_HOSTED_APPSERVER"] = "1" };

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay);

        await Assert.That(childEnv["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    [NotInParallel]
    public async Task Missing_hooks_on_the_app_server_route_fails_closed() {
        using var wt   = new TempDir(); // empty — no worktree hooks

        // Both hook scopes have to be genuinely absent or the fail-closed assertion proves nothing:
        // the worktree above is empty, and so is the home this points CODEX_HOME into.
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("spawn must never be reached when hooks are missing");
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<CodexHooksNotInstalledException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None));
    }

    [Test]
    [NotInParallel]
    public async Task Handshake_failure_disposes_the_spawned_child() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer { ThreadId = "" }; // thread/start returns no id -> StartAsync throws

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        var process = new FakeAcpProcess();
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), process));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard));

        // The factory disposed the runtime on the failed handshake, so the spawned child is not leaked.
        await Assert.That(process.HasExited).IsTrue();
    }

    static void WriteWorktreeHooks(TempDir worktree) =>
        worktree.CreateFile([".codex", "hooks.json"], """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}]
            }}
            """);
}
