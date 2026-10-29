using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

/// <summary>Turns a launch request into a running Muse runtime on the right lane, against a scripted
/// <c>muse serve</c>. Every spawned process is the fake; the marker store and the reviewer version
/// store are per-test directories.</summary>
public class MuseHostedAgentRuntimeFactoryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    const string kcap = "/usr/local/bin/kcap";
    const string SearchTool = "mcp__kcap_sessions__search_sessions";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  KcapConfig { get; init; }
    [TempHome]        public required TempHome        Home { get; init; }

    sealed record Relay(string CapacitorPath, JsonObject Payload, string SessionId, string AgentId, string? ServerUrl, string DaemonId, string DaemonEpoch);

    sealed class Rig {
        public required MuseScriptedHost Host { get; init; }
        public required MuseHostedAgentRuntimeFactory Factory { get; init; }
        public List<ProcessStartInfo> Spawned { get; } = [];
        public List<Relay> Relays { get; } = [];
        public List<string[]> MarkerFilesAtSpawn { get; } = [];
    }

    DaemonConfig Config(bool reviewerEnabled = true) => new() {
        Name                          = "test-daemon",
        ServerUrl                     = "http://127.0.0.1:1",
        MusePath                      = "/opt/muse/bin/muse",
        MuseUnattendedReviewerEnabled = reviewerEnabled,
        Store                         = Daemons.Store,
        ConfigRoot                    = KcapConfig.Root,
        Home                          = Home,
        Version                       = "9.9.9",
        Profiles                      = Resolutions.At("http://kcap.test", KcapConfig.Root, UrlSource.Environment),
    };

    string MarkerDir => MuseHostedSessionMarker.DirectoryFor(Home);

    static RuntimeStartContext Ctx(
            bool         isReviewFlow     = false,
            bool         isReview         = false,
            WorkLocation work             = WorkLocation.OwnedWorktree,
            bool         borrowedSnapshot = false,
            string?      prompt           = "do work",
            string[]?    allowlist        = null,
            string?      model            = null,
            string?      resume           = null) => new(
        AgentId: "agent-1", Vendor: "muse", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: prompt,
        Model: model, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: "http://kcap.test", DaemonBridgeUrl: null, CapacitorPath: kcap,
        McpAllowlist: allowlist, Work: work, DaemonId: "daemon-1", DaemonEpoch: "epoch-1",
        IsBorrowedSnapshot: borrowedSnapshot, ResumeSessionId: resume);

    Rig NewRig(
            DaemonConfig?     config         = null,
            bool              hooksInstalled = true,
            MuseSandboxProbe? sandbox        = null,
            string?           installed      = "1.4.7",
            string?           affirmed       = "1.4.7",
            bool              binary         = true,
            Action<MuseScriptedHost>? configure = null,
            TimeProvider?     time           = null,
            TimeSpan?         readyDeadline  = null,
            Action?           onRelay        = null,
            Func<Relay, Task>? relayOutcome  = null) {
        config ??= Config();
        if (affirmed is not null) new ReviewerVersionStore(config.Store.StateDirectory(config.Name), DaemonRunner.MuseVendor).Affirm(affirmed);

        var host = new MuseScriptedHost();
        configure?.Invoke(host);

        Rig? rig = null;
        var connection = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        var factory = new MuseHostedAgentRuntimeFactory(
            config, NullLoggerFactory.Instance, time ?? TimeProvider.System, connection,
            new MusePaths(Home, null, null),
            processSource: (psi, _) => {
                rig!.Spawned.Add(psi);
                rig.MarkerFilesAtSpawn.Add(Directory.Exists(MarkerDir) ? Directory.GetFiles(MarkerDir) : []);
                return Task.FromResult<IMuseMspProcess>(host.Process);
            },
            binaryExists: _ => binary,
            resolveVersion: _ => installed,
            posixHost: true,
            sandbox: sandbox ?? new MuseSandboxProbe(() => true),
            hooksInstalled: _ => hooksInstalled,
            hookRelay: (kcap, payload, sessionId, agentId, url, daemonId, epoch, _, _) => {
                var relay = new Relay(kcap, payload, sessionId, agentId, url, daemonId, epoch);
                lock (rig!.Relays) rig.Relays.Add(relay);
                onRelay?.Invoke();
                return relayOutcome?.Invoke(relay) ?? Task.CompletedTask;
            },
            readyDeadline: readyDeadline);

        rig = new Rig { Host = host, Factory = factory };
        return rig;
    }

    static JsonObject Params(JsonObject request) => request["params"]!.AsObject();

    static async Task<HostedRuntimeStart> StartAsync(Rig rig, RuntimeStartContext ctx) =>
        await rig.Factory.StartAsync(ctx, CancellationToken.None).WaitAsync(HangGuard);

    [Test]
    public async Task Interactive_resume_is_supported_and_review_flow_is_not() {
        const string resumed = "01a13695-0736-75d0-8f4e-f121585b8453";
        var rig = NewRig();

        await Assert.That(rig.Factory.SupportsRestartResume(null)).IsTrue();

        var start = await StartAsync(rig, Ctx(resume: resumed));
        await using var _ = start.Runtime;
        var resume = Params(await rig.Host.WaitForWriteAsync("session/resume"));
        await Assert.That((string?)resume["sessionId"]).IsEqualTo(resumed);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StartAsync(NewRig(), Ctx(isReviewFlow: true, prompt: "review", allowlist: ["kcap-sessions"], resume: resumed)));
        await Assert.That(refused!.Message).StartsWith("muse_reviewer_resume_unsupported");
    }

    [Test]
    public async Task IsAvailable_follows_the_binary() {
        await Assert.That(NewRig(binary: true).Factory.IsAvailable()).IsTrue();
        await Assert.That(NewRig(binary: false).Factory.IsAvailable()).IsFalse();
    }

    [Test]
    public async Task The_factory_advertises_muse_with_an_independent_snapshot_for_borrowed_review() {
        var factory = NewRig().Factory;

        await Assert.That(factory.Vendor).IsEqualTo("muse");
        await Assert.That(factory.CliPath).IsEqualTo("/opt/muse/bin/muse");
        await Assert.That(factory.SupportsUnattended).IsTrue();
        await Assert.That(factory.SupportsBorrowedReviewFlow).IsTrue();
        await Assert.That(factory.BorrowedReviewContainment).IsEqualTo("independent-snapshot");
        await Assert.That(factory.BorrowedReviewRequiresIndependentSnapshot).IsTrue();
        await Assert.That(factory.SupportsPrReview).IsFalse();
        await Assert.That(factory.SupportsModelSelection).IsTrue();
        await Assert.That(await factory.ProbeModelsAsync(CancellationToken.None)).IsNull();
        await Assert.That(factory.CatalogFingerprintPaths).IsEquivalentTo(new[] {
            Home.PathTo(".config", "muse", "settings.json"), Home.PathTo(".config", "muse", "auth.json") });
    }

    [Test]
    public async Task The_factory_advertises_the_models_muse_cached_and_fingerprints_the_cache() {
        var file = Home.CreateFile([".local", "share", "muse", "model-catalog", "6d657461__p746268.json"], """
            {"schema_version":2,"provider_id":"meta","rows":[
              {"model_id":"muse-spark-1.3","display_label":"muse-spark-1.3","visibility":"visible","release_date":"2026-09-02"},
              {"model_id":"muse-spark-1.3-contributor","display_label":"muse-spark-1.3-contributor","visibility":"hidden","release_date":"2026-09-02"},
              {"model_id":"muse-spark-1.2","display_label":"muse-spark-1.2","visibility":"visible","release_date":"2026-08-05"}]}
            """);
        var factory = NewRig().Factory;

        var models = (await factory.ProbeModelsAsync(CancellationToken.None))!;

        await Assert.That(models.Select(m => m.Value)).IsEquivalentTo(["muse-spark-1.3", "muse-spark-1.2"], CollectionOrdering.Matching);
        await Assert.That(factory.CatalogFingerprintPaths).Contains(file);
    }

    [Test]
    public async Task Interactive_plan_asks_me_on_request_and_no_mcp() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx(model: "muse-spark-1.3"));
        await using var _ = start.Runtime;

        var session = Params(await rig.Host.WaitForWriteAsync("session/start"));
        await Assert.That((string?)session["permissionProfile"]).IsEqualTo(":ask-me");
        await Assert.That((string?)session["approvalMode"]).IsEqualTo("onRequest");
        await Assert.That(session["config"]).IsNull();
        await Assert.That((string?)session["modelId"]).IsEqualTo("muse-spark-1.3");

        var turn = Params(await rig.Host.WaitForWriteAsync("turn/start"));
        await Assert.That((string?)turn["input"]![0]!["text"]).IsEqualTo("do work");

        await Assert.That(start.Transcript).IsNull();
        await Assert.That(start.McpConfigPath).IsNull();

        var psi = rig.Spawned.Single();
        await Assert.That(psi.FileName).IsEqualTo("/opt/muse/bin/muse");
        await Assert.That(string.Join(" ", psi.ArgumentList))
            .IsEqualTo("serve --provider meta --model muse-spark-1.3 --trust-workspace --sandbox-network proxy-only");
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/abs/wt");
        await Assert.That(psi.Environment["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_ID"]).IsEqualTo("daemon-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_EPOCH"]).IsEqualTo("epoch-1");
        await Assert.That(psi.Environment["KCAP_URL"]).IsEqualTo("http://kcap.test");
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
    }

    [Test]
    public async Task Reviewer_plan_has_read_only_on_request_and_required_mcp() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"]));
        await using var _ = start.Runtime;

        var session = Params(await rig.Host.WaitForWriteAsync("session/start"));
        await Assert.That((string?)session["permissionProfile"]).IsEqualTo(":read-only");
        await Assert.That((string?)session["approvalMode"]).IsEqualTo("onRequest");

        var servers = session["config"]!["mcpServers"]!.AsObject();
        await Assert.That(servers.Count).IsEqualTo(2);

        foreach (var (_, server) in servers) {
            await Assert.That((string?)server!["transport"]).IsEqualTo("stdio");
            await Assert.That((string?)server["command"]).IsEqualTo(kcap);
            await Assert.That((string?)server["mode"]).IsEqualTo("required");
        }

        var channel = servers.Single(s => s.Key.StartsWith("kcap_flow_result_", StringComparison.Ordinal)).Value!;
        await Assert.That(channel["args"]!.AsArray().Select(a => (string)a!)).IsEquivalentTo(new[] { "mcp", "flow-result" });
        await Assert.That((string?)channel["env"]!["KCAP_FLOW_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That((string?)channel["env"]!["KCAP_URL"]).IsEqualTo("http://kcap.test");

        var sessions = servers.Single(s => s.Key.StartsWith("kcap_sessions_", StringComparison.Ordinal)).Value!;
        await Assert.That(sessions["args"]!.AsArray().Select(a => (string)a!)).IsEquivalentTo(new[] { "mcp", "sessions" });

        await Assert.That(string.Join(" ", rig.Spawned.Single().ArgumentList))
            .IsEqualTo("serve --provider meta --sandbox-network restricted --disable-write");

        var turn = Params(await rig.Host.WaitForWriteAsync("turn/start"));
        await Assert.That((string?)turn["input"]![0]!["text"]).IsEqualTo("review this");
    }

    /// <summary>Muse passes an MCP child only <c>HOME</c> and the server's <c>env</c>, so a daemon whose
    /// config root is not the one its home derives must name it, or the result channel reads another
    /// profile. The rig's root is a separate temp directory, so it is non-default here.</summary>
    [Test]
    public async Task Reviewer_servers_carry_a_non_default_config_dir() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"]));
        await using var _ = start.Runtime;

        var servers = Params(await rig.Host.WaitForWriteAsync("session/start"))["config"]!["mcpServers"]!.AsObject();
        await Assert.That(servers.Count).IsEqualTo(2);

        foreach (var (_, server) in servers)
            await Assert.That((string?)server!["env"]![ConfigRoot.ConfigDirEnvVar]).IsEqualTo(KcapConfig.Root.Directory);
    }

    /// <summary>The twin: a root the home derives is what the child finds by itself, so nothing is added.</summary>
    [Test]
    public async Task Reviewer_servers_leave_the_default_config_dir_unset() {
        var config = Config();
        config.ConfigRoot = ConfigRoot.UnderHome(Home.Path);
        var rig = NewRig(config: config);

        var start = await StartAsync(rig, Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"]));
        await using var _ = start.Runtime;

        var servers = Params(await rig.Host.WaitForWriteAsync("session/start"))["config"]!["mcpServers"]!.AsObject();
        await Assert.That(servers.Count).IsEqualTo(2);

        foreach (var (_, server) in servers) {
            await Assert.That((string?)server!["env"]!["KCAP_URL"]).IsEqualTo("http://kcap.test");
            await Assert.That(server["env"]!.AsObject().ContainsKey(ConfigRoot.ConfigDirEnvVar)).IsFalse();
        }
    }

    /// <summary>A workspace that declares an MCP server under a kcap server's canonical name must not
    /// have its tools approved: the reviewer admits only this launch's aliased names.</summary>
    [Test]
    public async Task Reviewer_admits_its_servers_under_their_per_launch_alias_only() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"]));
        await using var _ = start.Runtime;

        var servers = Params(await rig.Host.WaitForWriteAsync("session/start"))["config"]!["mcpServers"]!.AsObject();
        var aliased = servers.Single(s => s.Key.StartsWith("kcap_sessions_", StringComparison.Ordinal)).Key;
        await Assert.That(aliased).IsNotEqualTo("kcap_sessions");

        rig.Host.Process.Push(MuseMspWire.ApprovalRequested.Replace(SearchTool, $"mcp__{aliased}__search_sessions"));
        var first = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        await Assert.That((string?)first["choiceId"]).IsEqualTo("allow_once");

        rig.Host.Process.Push(MuseMspWire.ApprovalRequested.Replace("01a13695-6ad8-7313-9d2b-57f03aebbb24", "01a13695-6ad8-7313-9d2b-57f03aebbb25"));
        var second = Params(await rig.Host.WaitForWriteAsync("approval/decide", 2));
        await Assert.That((string?)second["choiceId"]).IsEqualTo("abort");
    }

    [Test]
    public async Task Failed_bubblewrap_probe_refuses_reviewer_only() {
        var rig = NewRig(sandbox: new MuseSandboxProbe(() => false));

        var support = rig.Factory.DescribeUnattendedSupport();
        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason).StartsWith("muse_reviewer_sandbox_unavailable");

        var reviewer = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None));
        await Assert.That(reviewer!.Message).StartsWith("muse_reviewer_sandbox_unavailable");
        await Assert.That(rig.Spawned).IsEmpty();

        var start = await StartAsync(rig, Ctx());
        await using var _ = start.Runtime;
        await Assert.That(start.Runtime.Vendor).IsEqualTo("muse");
    }

    [Test]
    public async Task Host_reporting_sandbox_unavailable_refuses_the_reviewer() {
        var rig = NewRig(configure: h => h.Process.AutoInitializeResult = MuseMspWire.InitializeResult
            .Replace("\"sandboxAvailability\":\"available\"", "\"sandboxAvailability\":\"unavailable\""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("muse_reviewer_sandbox_unavailable");
        await Assert.That(rig.Host.Process.DisposeCalls).IsGreaterThanOrEqualTo(1);
        await Assert.That(rig.Host.WritesOf("turn/start")).IsEmpty();
    }

    [Test]
    public async Task Host_reporting_sandbox_unavailable_still_starts_an_interactive_agent() {
        var rig = NewRig(configure: h => h.Process.AutoInitializeResult = MuseMspWire.InitializeResult
            .Replace("\"sandboxAvailability\":\"available\"", "\"sandboxAvailability\":\"unavailable\""));

        var start = await StartAsync(rig, Ctx());
        await using var _ = start.Runtime;

        await Assert.That(rig.Host.WritesOf("turn/start").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Marker_is_written_before_spawn_and_carries_the_minted_id() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx());
        await using var _ = start.Runtime;

        var atSpawn = rig.MarkerFilesAtSpawn.Single();
        await Assert.That(atSpawn.Length).IsEqualTo(1);

        var minted = (string)Params(await rig.Host.WaitForWriteAsync("session/start"))["sessionId"]!;
        var marker = MuseHostedSessionMarker.TryRead(Home, minted);

        await Assert.That(marker).IsNotNull();
        await Assert.That(Path.GetFileName(atSpawn[0])).IsEqualTo(Guid.Parse(minted).ToString("N") + ".json");
        await Assert.That(marker!.AgentId).IsEqualTo("agent-1");
        await Assert.That(marker.DaemonId).IsEqualTo("daemon-1");
        await Assert.That(marker.DaemonEpoch).IsEqualTo("epoch-1");
        await Assert.That(Guid.Parse(minted).Version).IsEqualTo(7);
    }

    /// <summary>An installed hook runs with a cleared environment, so a daemon whose config root is not the
    /// home's names its root and server in the marker; the rig's root is a separate temp directory.</summary>
    [Test]
    public async Task Marker_carries_a_non_default_config_root_and_the_server() {
        var marker = await MarkerOfAsync(NewRig());

        await Assert.That(marker.ConfigDir).IsEqualTo(KcapConfig.Root.Directory);
        await Assert.That(marker.ServerUrl).IsEqualTo("http://kcap.test");
        await Assert.That(marker.Profile).IsNull();
    }

    /// <summary>A default-root daemon pointed at its server by <c>KCAP_URL</c> talks to a server the root's
    /// active profile need not name, so the marker carries the server even with no root to name.</summary>
    [Test]
    public async Task Marker_carries_the_server_of_a_default_root_daemon_chosen_by_url() {
        var config = Config();
        config.ConfigRoot = ConfigRoot.UnderHome(Home.Path);

        var marker = await MarkerOfAsync(NewRig(config: config));

        await Assert.That(marker.ConfigDir).IsNull();
        await Assert.That(marker.ServerUrl).IsEqualTo("http://kcap.test");
        await Assert.That(marker.Profile).IsNull();
    }

    /// <summary>A daemon that resolved its server through a profile names the profile, whose credential
    /// the hook needs as much as its server.</summary>
    [Test]
    public async Task Marker_names_the_profile_the_daemon_resolved_through() {
        var config = Config();
        config.ConfigRoot = ConfigRoot.UnderHome(Home.Path);
        config.Profiles   = Resolutions.Of(new Profile { ServerUrl = "http://kcap.test" }, "team-b");

        var marker = await MarkerOfAsync(NewRig(config: config));

        await Assert.That(marker.Profile).IsEqualTo("team-b");
    }

    /// <summary>A relative <c>KCAP_CONFIG_DIR</c> means nothing to a hook or MCP child running elsewhere, so
    /// both the marker and the reviewer's servers carry it fully qualified.</summary>
    [Test]
    public async Task A_relative_config_root_is_recorded_fully_qualified() {
        var config = Config();
        config.ConfigRoot = new ConfigRoot("relative-kcap-config");
        var expected = Path.GetFullPath("relative-kcap-config");
        var rig = NewRig(config: config);

        var start = await StartAsync(rig, Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"]));
        await using var _ = start.Runtime;

        var session = Params(await rig.Host.WaitForWriteAsync("session/start"));
        var marker  = MuseHostedSessionMarker.TryRead(Home, (string)session["sessionId"]!)!;
        await Assert.That(marker.ConfigDir).IsEqualTo(expected);
        await Assert.That(Path.IsPathFullyQualified(marker.ConfigDir!)).IsTrue();

        foreach (var (_, server) in session["config"]!["mcpServers"]!.AsObject())
            await Assert.That((string?)server!["env"]![ConfigRoot.ConfigDirEnvVar]).IsEqualTo(expected);
    }

    async Task<MuseHostedSessionMarker> MarkerOfAsync(Rig rig) {
        var start = await StartAsync(rig, Ctx());
        await using var _ = start.Runtime;

        var minted = (string)Params(await rig.Host.WaitForWriteAsync("session/start"))["sessionId"]!;
        return MuseHostedSessionMarker.TryRead(Home, minted)!;
    }

    /// <summary>Muse clears a hook's environment, so the hook finds the marker only under the root derived
    /// from the home — never under the daemon's own config dir, which here is somewhere else.</summary>
    [Test]
    public async Task A_daemon_under_another_config_dir_writes_the_marker_where_a_cleared_environment_hook_reads_it() {
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx());
        await using var _ = start.Runtime;

        var minted = (string)Params(await rig.Host.WaitForWriteAsync("session/start"))["sessionId"]!;

        await Assert.That(KcapConfig.Root.Directory).IsNotEqualTo(ConfigRoot.UnderHome(Home.Path).Directory);
        await Assert.That(File.Exists(Home.PathTo(".config", "kcap", "muse-hosted", Guid.Parse(minted).ToString("N") + ".json"))).IsTrue();
        await Assert.That(Directory.Exists(KcapConfig.Root.Path("muse-hosted"))).IsFalse();
    }

    [Test]
    public async Task A_resumed_session_is_reopened_under_its_own_id() {
        const string resumed = "01a13695-0736-75d0-8f4e-f121585b8453";
        var rig = NewRig();

        var start = await StartAsync(rig, Ctx(resume: resumed.Replace("-", "")));
        await using var _ = start.Runtime;

        var resume = Params(await rig.Host.WaitForWriteAsync("session/resume"));
        await Assert.That((string?)resume["sessionId"]).IsEqualTo(resumed);
        await Assert.That(rig.Host.WritesOf("session/start")).IsEmpty();
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, resumed)?.AgentId).IsEqualTo("agent-1");
    }

    [Test]
    public async Task Relay_runs_when_hooks_are_not_installed_and_not_when_they_are() {
        var relayed = NewRig(hooksInstalled: false);
        var start = await StartAsync(relayed, Ctx());

        var minted = (string)Params(await relayed.Host.WaitForWriteAsync("session/start"))["sessionId"]!;
        var sessionStart = relayed.Relays.Single();
        await Assert.That(sessionStart.CapacitorPath).IsEqualTo(kcap);
        await Assert.That(sessionStart.SessionId).IsEqualTo(minted);
        await Assert.That(sessionStart.AgentId).IsEqualTo("agent-1");
        await Assert.That(sessionStart.ServerUrl).IsEqualTo("http://kcap.test");
        await Assert.That(sessionStart.DaemonId).IsEqualTo("daemon-1");
        await Assert.That(sessionStart.DaemonEpoch).IsEqualTo("epoch-1");
        await Assert.That((string?)sessionStart.Payload["hook_event_name"]).IsEqualTo("SessionStart");
        await Assert.That((string?)sessionStart.Payload["source"]).IsEqualTo("startup");
        await Assert.That((string?)sessionStart.Payload["session_id"]).IsEqualTo(minted);
        await Assert.That((string?)sessionStart.Payload["cwd"]).IsEqualTo("/abs/wt");
        await Assert.That((string?)sessionStart.Payload["model_provider"]).IsEqualTo("meta");
        await Assert.That((string?)sessionStart.Payload["transcript_path"]).EndsWith("session.jsonl");

        await start.Runtime.DisposeAsync();

        await Assert.That(relayed.Relays.Count).IsEqualTo(2);
        await Assert.That((string?)relayed.Relays[1].Payload["hook_event_name"]).IsEqualTo("SessionEnd");
        await Assert.That((string?)relayed.Relays[1].Payload["session_id"]).IsEqualTo(minted);

        var hooked = NewRig(hooksInstalled: true);
        var quiet  = await StartAsync(hooked, Ctx());
        await quiet.Runtime.DisposeAsync();

        await Assert.That(hooked.Relays).IsEmpty();
    }

    /// <summary>The relayed end is the session's only end when no hooks are installed, so a daemon
    /// shutting down must not finish disposing before it lands.</summary>
    [Test]
    public async Task Dispose_waits_for_the_relayed_session_end() {
        var endStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endGate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = NewRig(hooksInstalled: false, relayOutcome: relay => {
            if ((string?)relay.Payload["hook_event_name"] != "SessionEnd") return Task.CompletedTask;

            endStarted.TrySetResult();
            return endGate.Task;
        });

        var start   = await StartAsync(rig, Ctx());
        var dispose = start.Runtime.DisposeAsync().AsTask();

        await endStarted.Task.WaitAsync(HangGuard);
        await Assert.That(dispose.IsCompleted).IsFalse();

        endGate.SetResult();
        await dispose.WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_failing_session_end_relay_does_not_escape_dispose() {
        var rig = NewRig(hooksInstalled: false, relayOutcome: relay =>
            (string?)relay.Payload["hook_event_name"] == "SessionEnd"
                ? Task.FromException(new IOException("relay failed"))
                : Task.CompletedTask);

        var start = await StartAsync(rig, Ctx());
        await start.Runtime.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(rig.Relays.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pr_review_is_refused() {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Factory.StartAsync(Ctx(isReview: true), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_pr_review_unsupported");
        await Assert.That(rig.Spawned).IsEmpty();
    }

    [Test]
    public async Task Borrowed_workspace_without_snapshot_is_refused() {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(work: WorkLocation.BorrowedCwd), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_requires_owned_worktree");
        await Assert.That(rig.Spawned).IsEmpty();
        await Assert.That(Directory.Exists(MarkerDir) && Directory.GetFiles(MarkerDir).Length > 0).IsFalse();
    }

    [Test]
    public async Task A_borrowed_snapshot_is_refused_on_the_interactive_lane() {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(work: WorkLocation.BorrowedCwd, borrowedSnapshot: true), CancellationToken.None));

        await Assert.That(ex!.Message).IsEqualTo("borrowed_snapshot_containment_mismatch");
    }

    [Test]
    public async Task A_disabled_reviewer_is_refused_before_anything_spawns() {
        var rig = NewRig(config: Config(reviewerEnabled: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_reviewer_disabled");
        await Assert.That(rig.Spawned).IsEmpty();
    }

    [Test]
    public async Task A_build_below_the_floor_withholds_the_reviewer() =>
        await Assert.That(NewRig(installed: "1.4.6").Factory.DescribeUnattendedSupport().WithheldReason)
            .StartsWith("muse_reviewer_below_verified_floor");

    [Test]
    public async Task A_missing_binary_withholds_the_reviewer() =>
        await Assert.That(NewRig(binary: false).Factory.DescribeUnattendedSupport().WithheldReason)
            .StartsWith("muse_reviewer_binary_missing");

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("/review")]
    public async Task A_reviewer_without_a_plain_first_prompt_is_refused(string prompt) {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(isReviewFlow: true, prompt: prompt), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_reviewer_");
        await Assert.That(rig.Spawned).IsEmpty();
    }

    /// <summary>session/resume cannot carry the reviewer's posture, so a reviewer never resumes.</summary>
    [Test]
    public async Task A_reviewer_resume_is_refused_before_anything_spawns() {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Factory.StartAsync(
            Ctx(isReviewFlow: true, prompt: "review this", allowlist: ["kcap-sessions"], resume: "01a13695-0736-75d0-8f4e-f121585b8453"),
            CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_reviewer_resume_unsupported");
        await Assert.That(rig.Spawned).IsEmpty();
        await Assert.That(Directory.Exists(MarkerDir) ? Directory.GetFiles(MarkerDir) : []).IsEmpty();
    }

    [Test]
    public async Task A_reviewer_allowlist_naming_a_writing_server_is_refused() {
        var rig = NewRig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(isReviewFlow: true, allowlist: ["kcap-memory"]), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("muse_reviewer_allowlist_rejected");
    }

    [Test]
    public async Task A_launch_whose_session_never_opens_is_disposed_and_leaves_no_marker() {
        var rig = NewRig(configure: h => h.Override = request =>
            (string?)request["method"] == "session/start"
                ? MuseScriptedHost.ErrorResponse((long)request["id"]!, "no")
                : null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(rig.Host.Process.DisposeCalls).IsGreaterThanOrEqualTo(1);
        await Assert.That(Directory.GetFiles(MarkerDir)).IsEmpty();
    }

    [Test]
    public async Task A_host_that_never_acknowledges_the_first_prompt_fails_the_launch_at_the_ready_deadline() {
        var time = new FakeTimeProvider();
        var rig  = NewRig(configure: h => h.Silent.Add("turn/start"), time: time, readyDeadline: TimeSpan.FromSeconds(5));

        var launch = rig.Factory.StartAsync(Ctx(), CancellationToken.None);
        await rig.Host.WaitForWriteAsync("turn/start");

        // The deadline's timer is armed just after the write the test observed, so advance until it fires.
        using var guard = new CancellationTokenSource(HangGuard);
        while (!launch.IsCompleted) {
            guard.Token.ThrowIfCancellationRequested();
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(launch, Task.Delay(20, guard.Token));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await launch);
        await Assert.That(ex!.Message).StartsWith("muse_first_turn_unacknowledged");
        await Assert.That(rig.Host.Process.DisposeCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Cancelling_while_the_first_prompt_is_unacknowledged_aborts_the_launch() {
        var rig = NewRig(configure: h => h.Silent.Add("turn/start"));
        using var cts = new CancellationTokenSource();

        var launch = rig.Factory.StartAsync(Ctx(), cts.Token);
        await rig.Host.WaitForWriteAsync("turn/start");
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await launch.WaitAsync(HangGuard));
        await Assert.That(rig.Host.Process.DisposeCalls).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>The relay swallows its own cancellation, so the launch must notice it afterwards rather than
    /// go on to send the first prompt.</summary>
    [Test]
    public async Task A_launch_cancelled_during_the_relay_sends_no_prompt() {
        using var cts = new CancellationTokenSource();
        var rig = NewRig(hooksInstalled: false, onRelay: cts.Cancel);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await rig.Factory.StartAsync(Ctx(), cts.Token).WaitAsync(HangGuard));

        await Assert.That(rig.Relays).Count().IsGreaterThanOrEqualTo(1);
        await Assert.That(rig.Host.WritesOf("turn/start")).IsEmpty();
    }

    [Test]
    public async Task A_throwing_sandbox_probe_withholds_the_reviewer() {
        var rig = NewRig(sandbox: new MuseSandboxProbe(() => throw new InvalidOperationException("probe failed")));

        await Assert.That(rig.Factory.SupportsUnattended).IsFalse();
        await Assert.That(rig.Factory.DescribeUnattendedSupport().WithheldReason).StartsWith("muse_reviewer_sandbox_unavailable");
    }

    [Test]
    public async Task Construction_sweeps_stale_markers() {
        MuseHostedSessionMarker.Write(Home, new(
            "01a13695-0736-75d0-8f4e-f121585b8453", "old", "d", "e", TimeProvider.System.GetUtcNow() - TimeSpan.FromDays(3)));

        _ = NewRig();

        await Assert.That(Directory.GetFiles(MarkerDir)).IsEmpty();
    }
}
