using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The composite codex factory's routing: a pinned transport first, then the launch class, then the
/// switch the server last acknowledged. The app-server route is exercised through the spawn seam (a
/// fake peer); the routing table runs a stub Codex for the version a pinned launch probes.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
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
        public bool ResumesIdleWithoutPrompt(string? runtimeTransport) => true;
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
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example", Home = Home },
            TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [],
        };

    static RuntimeStartContext Ctx(bool isReviewFlow, string worktreePath) => new(
        AgentId: "agent-1", Vendor: "codex", SourceRepoPath: worktreePath,
        Worktree: new WorktreeInfo(Path: worktreePath, Branch: "wt", SourceRepo: worktreePath),
        Prompt: null, Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "https://t.example", DaemonBridgeUrl: null, CapacitorPath: "/opt/kcap");

    DaemonConfig ConfigWith(bool switchOn, string? version = "0.146.0") {
        var resolution = CodexTransportResolution.Resolve(1, switchOn, () => version);
        return new DaemonConfig {
            CodexResolution             = resolution,
            AcknowledgedCodexResolution = resolution,
            Version                     = "0.146.0",
            Home                        = Home,
        };
    }

    CodexHostedAgentRuntimeFactory Factory(
            RecordingPtyFactory pty, bool appServerActive, CodexAppServerSpawnFactory? spawn = null, AccountReadingSink? readings = null) =>
        new(NewLauncher(), pty, ConfigWith(switchOn: appServerActive), NullLoggerFactory.Instance, TimeProvider.System, spawn,
            installedVersion: () => "0.146.0", readings: readings);

    /// <summary>A Codex at the factory's CLI path reporting <paramref name="version"/>, which a pinned
    /// app-server launch probes.</summary>
    string CodexReporting(string version) =>
        Tmp.CreateExecutable("codex", $"#!/bin/sh\necho 'codex-cli {version}'\n");

    // ── Routing decision ─────────────────────────────────────────────────────────────────────
    public enum Kind { Interactive, ReviewFlow, PrReview }

    /// <summary>The installed Codex reports <c>version</c> both to the resolution, when the switch is on,
    /// and at the factory's CLI path, which a pinned launch probes.</summary>
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
            NewLauncher(), new RecordingPtyFactory(), config, NullLoggerFactory.Instance, TimeProvider.System,
            installedVersion: () => "0.146.0");

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
            NewLauncher(), new RecordingPtyFactory(), config, NullLoggerFactory.Instance, TimeProvider.System,
            installedVersion: () => "0.146.0");

        var route = factory.Route(Ctx(kind == Kind.ReviewFlow, Home.PathTo("routing")));

        await Assert.That(route.AppServer).IsEqualTo(acknowledgedActive);
    }

    /// <summary>A launch naming no transport that follows an app-server advertisement is checked against the
    /// Codex installed now. Below the floor it is refused, never moved to PTY, since the server expects
    /// app-server, and the daemon is asked to re-advertise.</summary>
    [Test]
    [Arguments("0.146.0", false)]
    [Arguments("0.145.9", true)]
    public async Task An_unpinned_app_server_launch_checks_the_installed_codex(string installed, bool refused) {
        var refreshes = new List<string>();
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(), ConfigWith(switchOn: true), NullLoggerFactory.Instance, TimeProvider.System,
            refreshCapabilities: refreshes.Add, installedVersion: () => installed);

        var route = factory.Route(Ctx(isReviewFlow: false, Home.PathTo("routing")));

        await Assert.That(route.AppServer).IsEqualTo(!refused);
        await Assert.That(route.Refusal is not null).IsEqualTo(refused);
        await Assert.That(refreshes.Count).IsEqualTo(refused ? 1 : 0);
    }

    /// <summary>Codex can be upgraded or downgraded between capability refreshes, so the resolution's
    /// version cannot decide a pinned launch either way.</summary>
    [Test]
    [Arguments("0.146.0", "0.145.9", true)]
    [Arguments("0.145.9", "0.146.0", false)]
    public async Task A_pinned_app_server_launch_checks_the_installed_codex_not_the_resolution(
            string resolved, string installed, bool refused) {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub Codex is a POSIX shell script.");
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(CodexReporting(installed)), ConfigWith(switchOn: true, resolved),
            NullLoggerFactory.Instance, TimeProvider.System);

        var route = factory.Route(Ctx(isReviewFlow: false, Home.PathTo("routing")) with { ExpectedRuntimeTransport = "app-server" });

        await Assert.That(route.AppServer).IsEqualTo(!refused);
        await Assert.That(route.Refusal is not null).IsEqualTo(refused);
    }

    [Test]
    public async Task A_pinned_app_server_launch_below_the_floor_is_refused_without_starting_pty() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub Codex is a POSIX shell script.");
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory(CodexReporting("0.145.9"));
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

    /// <summary>Only a session recorded on the terminal resumes there with no turn; an app-server session, or one
    /// whose transport was never recorded and so may take the app-server route, keeps the nudge.</summary>
    [Test]
    [Arguments("pty", true)]
    [Arguments("app-server", false)]
    [Arguments("pending", false)]
    [Arguments(null, false)]
    public async Task Only_a_terminal_session_resumes_without_a_prompt(string? transport, bool silent) {
        await Assert.That(((IHostedAgentRuntimeFactory)Factory(new RecordingPtyFactory(), appServerActive: true)).ResumesIdleWithoutPrompt(transport))
            .IsEqualTo(silent);
    }

    [Test]
    [NotInParallel]
    public async Task App_server_resume_still_sends_thread_resume() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
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
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
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

    /// <summary>The non-default account carries a later generation, so the reading landing at that
    /// generation proves the runtime posted the launch's resolved account, not the default's.</summary>
    [Test]
    [NotInParallel]
    public async Task App_server_rateLimits_update_lands_on_the_agents_account_not_the_default() {
        using var wt = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        const string defaultId = "c0dec001-default";
        const string workId    = "c0dec002-work";
        var store      = new AccountStore(Home.PathTo("accounts"));
        var defaultDir = AccountDirectory.Normalize(Home.CreateDir(".codex"));
        store.Mutate(r => (r with {
            Accounts = [
                new VendorAccount(defaultId, HarnessId.Codex, defaultDir, "default", DateTimeOffset.UnixEpoch),
                new VendorAccount(workId, HarnessId.Codex, AccountDirectory.Normalize(Home.CreateDir(".codex-work")), "work", DateTimeOffset.UnixEpoch, Generation: 2),
            ],
        }, 0));
        var account = AccountResolution.Resolve(store.Load(), HarnessId.Codex, workId, defaultDir).Account!;
        using var sink = new AccountReadingSink(store, TimeProvider.System, NullLogger<AccountReadingSink>.Instance);

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam, readings: sink);

        var start = await factory.StartAsync(Ctx(isReviewFlow: false, wt.Path) with { Account = account }, CancellationToken.None).WaitAsync(HangGuard);
        try {
            await fake.SendNotificationAsync("account/rateLimits/updated", new JsonObject {
                ["rateLimits"] = new JsonObject {
                    ["limitId"] = "codex",
                    ["primary"] = new JsonObject { ["usedPercent"] = 42, ["windowDurationMins"] = 300, ["resetsAt"] = 1_800_000_000 },
                },
            });

            var deadline = DateTime.UtcNow + HangGuard;
            ReadingFile? file;
            while ((file = store.Readings.LoadAll().GetValueOrDefault(workId)) is null) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("the rate-limit notification recorded nothing");
                await Task.Delay(10);
            }

            await Assert.That(account.Generation).IsEqualTo(2L);
            await Assert.That(file.Reading.Generation).IsEqualTo(2L);
            await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(42.0);
            await Assert.That(store.Readings.LoadAll().ContainsKey(defaultId)).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
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

        CodexAppServerSpawnFactory seam = (_, _, _, _, env, _, _, _) => {
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

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, new Dictionary<string, string> { ["KCAP_SESSION_KIND"] = "hosted_agent" }, []);

        await Assert.That(childEnv.ContainsKey("KCAP_FLOW_ROLE")).IsFalse();
    }

    [Test]
    public async Task ApplyChildEnv_clears_an_inherited_marker_when_the_overlay_does_not_emit() {
        // A daemon whose OWN environment carries the marker must not leak it to a non-emitting child —
        // that child's hook would suppress the only watcher and lose the transcript.
        var childEnv = new Dictionary<string, string?> { ["KCAP_HOSTED_APPSERVER"] = "1", ["PATH"] = "/usr/bin" };
        var overlay  = new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "agent-1" }; // dormant: no marker

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay, []);

        await Assert.That(childEnv.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
        await Assert.That(childEnv["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(childEnv["PATH"]).IsEqualTo("/usr/bin");
    }

    [Test]
    public async Task ApplyChildEnv_sets_the_marker_when_the_overlay_emits() {
        var childEnv = new Dictionary<string, string?>();
        var overlay  = new Dictionary<string, string> { ["KCAP_HOSTED_APPSERVER"] = "1" };

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay, []);

        await Assert.That(childEnv["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    [NotInParallel]
    public async Task Missing_hooks_on_the_app_server_route_fails_closed() {
        using var wt   = new TempDir(); // empty — no worktree hooks

        // Both hook scopes have to be genuinely absent or the fail-closed assertion proves nothing:
        // the worktree above is empty, and so is the home this points CODEX_HOME into.
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
            throw new InvalidOperationException("spawn must never be reached when hooks are missing");
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<CodexHooksNotInstalledException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None));
    }

    /// Hooks exist only in the account directory, so the start passes the preflight only when the
    /// launcher ran against the launch's account; the trust entry shows where it wrote.
    [Test]
    public async Task AppServer_factory_prepares_with_the_launch_account() {
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("codex-work");
        accountDir.CreateFile("hooks.json", """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}]
            }}
            """);
        await using var fake = new FakeCodexAppServer();

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);
        var ctx     = Ctx(isReviewFlow: false, worktree) with { Account = new ResolvedAccount(accountDir, "acct-1", "Work") };

        var start = await factory.StartAsync(ctx, CancellationToken.None).WaitAsync(HangGuard);
        await start.Runtime.DisposeAsync();

        await Assert.That(fake.ReceivedMethods).Contains("thread/start");
        await Assert.That(File.ReadAllText(AccountLayouts.Codex(Home, accountDir).ConfigToml))
            .Contains($"\"{CodexPaths.NormalizeProjectKey(worktree).Replace("\\", "\\\\")}\"");
        await Assert.That(File.Exists(Home.PathTo(".codex", "config.toml"))).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task Handshake_failure_disposes_the_spawned_child() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer { ThreadId = "" }; // thread/start returns no id -> StartAsync throws

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        var process = new FakeAcpProcess();
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), process));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard));

        // The factory disposed the runtime on the failed handshake, so the spawned child is not leaked.
        await Assert.That(process.HasExited).IsTrue();
    }

    /// <summary>The seam's overlay, and the environment the real spawn would hand this launch's child: the
    /// daemon's own, as a <see cref="ProcessStartInfo"/> seeds it, with the seam's removals and overlay applied.</summary>
    async Task<(IReadOnlyDictionary<string, string> Overlay, IDictionary<string, string?> Child)> AppServerChildEnvFor(ResolvedAccount account) {
        using var wt = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();
        IReadOnlyDictionary<string, string>? overlay = null;
        IReadOnlyCollection<string>?         remove  = null;

        CodexAppServerSpawnFactory seam = (_, _, _, _, env, removeEnv, _, _) => {
            (overlay, remove) = (env, removeEnv);
            return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        };
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path) with { Account = account }, CancellationToken.None).WaitAsync(HangGuard);
        await start.Runtime.DisposeAsync();

        var child = new ProcessStartInfo().Environment;
        CodexHostedAgentRuntimeFactory.ApplyChildEnv(child, overlay!, remove!);

        return (overlay!, child);
    }

    string CodexDaemonDefault => Home.CreateDir(".codex-work");

    [Test]
    [NotInParallel]
    public async Task Launch_resolved_to_the_built_in_directory_removes_the_inherited_variable() {
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", CodexDaemonDefault);

        var (_, child) = await AppServerChildEnvFor(new ResolvedAccount(Home.CreateDir(".codex"), "acct-personal", "Personal"));

        await Assert.That(child.ContainsKey("CODEX_HOME")).IsFalse();
        await Assert.That(Environment.GetEnvironmentVariable("CODEX_HOME")).IsEqualTo(CodexDaemonDefault);
    }

    [Test]
    [NotInParallel]
    public async Task Launch_resolved_to_the_daemons_own_custom_default_sets_it_explicitly() {
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", CodexDaemonDefault);

        var (overlay, child) = await AppServerChildEnvFor(new ResolvedAccount(CodexDaemonDefault, null, ".codex-work"));

        await Assert.That(overlay["CODEX_HOME"]).IsEqualTo(CodexDaemonDefault);
        await Assert.That(child["CODEX_HOME"]).IsEqualTo(CodexDaemonDefault);
        await Assert.That(Environment.GetEnvironmentVariable("CODEX_HOME")).IsEqualTo(CodexDaemonDefault);
    }

    [Test]
    [NotInParallel]
    public async Task Launch_resolved_to_another_custom_account_sets_that_directory() {
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", CodexDaemonDefault);
        var other = Home.CreateDir(".codex-other");

        var (_, child) = await AppServerChildEnvFor(new ResolvedAccount(other, "acct-other", "Other"));

        await Assert.That(child["CODEX_HOME"]).IsEqualTo(other);
        await Assert.That(Environment.GetEnvironmentVariable("CODEX_HOME")).IsEqualTo(CodexDaemonDefault);
    }

    /// The built-in directory is reached by removing the daemon's inherited variable and a custom one by
    /// naming it; each account's probe runs in a working directory of its own, and its answer lands
    /// under its own id.
    [Test]
    public async Task Probe_runs_once_per_codex_account_with_its_home_in_the_environment() {
        using var daemons = new TempDaemonStore();
        var builtIn = Home.PathTo(".codex");
        var work    = Home.PathTo(".codex-work");
        var spawns  = new ConcurrentQueue<(string Cwd, IReadOnlyDictionary<string, string> Env, IReadOnlyCollection<string> Remove)>();
        var fakes   = new ConcurrentQueue<FakeCodexAppServer>();

        CodexAppServerSpawnFactory seam = (_, _, _, cwd, env, removeEnv, _, _) => {
            spawns.Enqueue((cwd, env, removeEnv));
            var named = env.TryGetValue("CODEX_HOME", out var codexHome) ? Path.GetFileName(codexHome) : "built-in";
            var fake  = new FakeCodexAppServer {
                ModelPages = [new JsonArray(new JsonObject { ["id"] = named, ["model"] = named, ["displayName"] = named, ["hidden"] = false })],
            };
            fakes.Enqueue(fake);
            return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        };
        var factory = new CodexHostedAgentRuntimeFactory(
            NewLauncher(), new RecordingPtyFactory(),
            new DaemonConfig { Name = "test", Store = daemons.Store, Version = "0.146.0", Home = Home },
            NullLoggerFactory.Instance, TimeProvider.System, seam);

        try {
            var byAccount = await VendorModelCatalogs.ProbeByAccountAsync(
                factory, [("acct-personal", builtIn), ("acct-work", work)], CancellationToken.None).WaitAsync(HangGuard);

            await Assert.That(spawns.Count).IsEqualTo(2);
            var personal = spawns.Single(s => !s.Env.ContainsKey("CODEX_HOME"));
            var custom   = spawns.Single(s => s.Env.ContainsKey("CODEX_HOME"));
            await Assert.That(personal.Remove).Contains("CODEX_HOME");
            await Assert.That(custom.Env["CODEX_HOME"]).IsEqualTo(work);
            await Assert.That(custom.Remove).DoesNotContain("CODEX_HOME");
            await Assert.That(personal.Cwd).IsNotEqualTo(custom.Cwd);

            await Assert.That(byAccount["acct-personal"].Single().Value).IsEqualTo("built-in");
            await Assert.That(byAccount["acct-work"].Single().Value).IsEqualTo(".codex-work");
        } finally {
            foreach (var fake in fakes) await fake.DisposeAsync();
        }
    }

    [Test]
    public async Task ApplyChildEnv_removes_before_it_overlays() {
        var childEnv = new Dictionary<string, string?> { ["CODEX_HOME"] = "/inherited", ["PATH"] = "/usr/bin" };

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, new Dictionary<string, string>(), ["CODEX_HOME"]);
        await Assert.That(childEnv.ContainsKey("CODEX_HOME")).IsFalse();

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, new Dictionary<string, string> { ["CODEX_HOME"] = "/account" }, ["CODEX_HOME"]);
        await Assert.That(childEnv["CODEX_HOME"]).IsEqualTo("/account");
        await Assert.That(childEnv["PATH"]).IsEqualTo("/usr/bin");
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
