using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>The reviewer launch shape: argv order, the tool allowlist, the env a reviewer child gets
/// instead of an interactive one, and the launch that advertises Pi, verifies its live tool surface
/// and refuses before creating a directory.</summary>
public class PiReviewerLaunchTests {
    static RuntimeStartContext Ctx(
            string? model = null, string? prompt = "", bool isReview = false, bool isReviewFlow = false,
            WorkLocation work = WorkLocation.OwnedWorktree, string? serverUrl = "http://kcap.test",
            string daemonId = "", string daemonEpoch = "") => new(
        AgentId: "agent-1", Vendor: "pi", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: prompt,
        Model: model, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: serverUrl, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        Work: work, DaemonId: daemonId, DaemonEpoch: daemonEpoch);

    static readonly PiReviewerLaunchPaths Paths = new(
        Dir: "/state/pi-reviewers/x", Extension: "/state/pi-reviewers/x/kcap-reviewer.ts",
        Manifest: "/state/pi-reviewers/x/manifest.json", SystemPrompt: "/state/pi-reviewers/x/system-prompt.md",
        Sessions: "/state/pi-reviewers/x/sessions", Ready: "/state/pi-reviewers/x/ready.json");

    static readonly IReadOnlyList<PiReviewerTool> Tools = PiReviewerToolSurface.For(
        [new(KcapMcpRegistry.ReservedResultChannelId, "/usr/local/bin/kcap", ["mcp", "flow-result"], [])]);

    // The reviewer version store and the launch directory both live under
    // config.Store.StateDirectory(config.Name), so the config needs a scratch store — the same fixture
    // AntigravityReviewerLaunchTests uses.
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    DaemonConfig ConfigWithState(bool reviewerEnabled = true) => new() {
        PiPath                      = "pi",
        PiUnattendedReviewerEnabled = reviewerEnabled,
        Name                        = "test-daemon",
        DaemonEpoch                 = "epoch-1",
        Store                       = Daemons.Store
    };

    static string LaunchRoot(DaemonConfig config) => PiReviewerLaunchDir.RootFor(config.Store.StateDirectory(config.Name));

    (DaemonConfig Config, FakePiRpcProcess Process) ReadyConfigAndProcess(string activeJson) {
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");
        var process = new FakePiRpcProcess();
        WriteReadyOnHandshake(process, config, activeJson);
        return (config, process);
    }

    static PiRpcHostedAgentRuntimeFactory Factory(
            DaemonConfig config, FakePiRpcProcess process, string? installed = "0.86.0", bool posix = true, bool binary = true) =>
        new(config, NullLoggerFactory.Instance, TimeProvider.System,
            processSource: (_, _) => Task.FromResult<IPiRpcProcess>(process),
            binaryExists:  _ => binary,
            readyDeadline: TimeSpan.FromSeconds(2),
            resolveVersion: _ => installed,
            posixHost: posix);

    // The fake answers get_state on its own; this writes ready.json when it sees the get_state line,
    // because in production the reviewer extension has written it by then.
    static void WriteReadyOnHandshake(FakePiRpcProcess process, DaemonConfig config, string activeJson) =>
        process.OnWrite = json => {
            if (!json.Contains("\"type\":\"get_state\"")) return;
            var dir = Directory.EnumerateDirectories(LaunchRoot(config)).Single();
            File.WriteAllText(Path.Combine(dir, "ready.json"), activeJson);
        };

    const string AllFive = """{"active":["list_directory","read_file","search_files","send_flow_message","submit_review_result"]}""";

    // ── BuildPsi: the reviewer launch vector ──

    [Test]
    public async Task Reviewer_argv_is_byte_exact() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

        await Assert.That(psi.ArgumentList).IsEquivalentTo(new[] {
            "--mode", "rpc",
            "--no-approve", "--no-extensions", "-e", Paths.Extension,
            "--tools", "read_file,list_directory,search_files,submit_review_result,send_flow_message",
            "--no-context-files", "--no-skills", "--no-prompt-templates", "--no-themes",
            "--system-prompt", Paths.SystemPrompt, "--append-system-prompt", "",
            "--offline",
            "--session-dir", Paths.Sessions,
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reviewer_argv_appends_the_model_last() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true, model: "anthropic/x"), Paths, Tools);

        await Assert.That(psi.ArgumentList.TakeLast(2)).IsEquivalentTo(new[] { "--model", "anthropic/x" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reviewer_argv_never_carries_the_inert_builtin_flag() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

        await Assert.That(psi.ArgumentList).DoesNotContain("--no-builtin-tools");
    }

    [Test, NotInParallel]
    public async Task Reviewer_env_names_the_manifest_and_drops_the_variables_that_repoint_pi() {
        Environment.SetEnvironmentVariable("JITI_ALIAS", "x");
        Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", "1");

        try {
            var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

            await Assert.That(psi.Environment[PiReviewerExtension.ManifestEnvVar]).IsEqualTo(Paths.Manifest);
            foreach (var name in new[] { "PI_PACKAGE_DIR", "PI_EXPERIMENTAL", "PI_CODING_AGENT_SESSION_DIR" })
                await Assert.That(psi.Environment.ContainsKey(name)).IsFalse();
            await Assert.That(psi.Environment.Keys.Any(k => k.StartsWith("JITI_", StringComparison.Ordinal))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("JITI_ALIAS", null);
            Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", null);
        }
    }

    [Test]
    public async Task An_interactive_launch_is_unchanged() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo("--mode rpc");
        await Assert.That(psi.Environment.ContainsKey(PiReviewerExtension.ManifestEnvVar)).IsFalse();
    }

    // ── advertisement ──

    [Test]
    public async Task Advertises_when_every_gate_is_open() {
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");

        var support = Factory(config, new FakePiRpcProcess()).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsTrue();
        await Assert.That(support.WithheldReason).IsNull();
    }

    [Test]
    [Arguments(false, true,  true,  "0.86.0", "pi_reviewer_disabled")]
    [Arguments(true,  false, true,  "0.86.0", "pi_reviewer_unsupported_platform")]
    [Arguments(true,  true,  false, "0.86.0", "pi_reviewer_binary_missing")]
    [Arguments(true,  true,  true,  "0.84.0", "pi_reviewer_below_verified_floor")]
    [Arguments(true,  true,  true,  null,     "pi_reviewer_version_unresolved")]
    public async Task Withholds_with_a_coded_reason(bool enabled, bool posix, bool binary, string? installed, string code) {
        var config = ConfigWithState(reviewerEnabled: enabled);
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");

        var support = Factory(config, new FakePiRpcProcess(), installed, posix, binary).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).StartsWith(code);
    }

    [Test]
    public async Task A_disabled_reviewer_is_never_version_probed() {
        var probed = false;
        var config = ConfigWithState(reviewerEnabled: false);
        var factory = new PiRpcHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance, TimeProvider.System,
            binaryExists: _ => true, resolveVersion: _ => { probed = true; return "0.86.0"; }, posixHost: true);

        factory.DescribeUnattendedSupport();

        await Assert.That(probed).IsFalse();
    }

    // ── the ladder is reviewer-only ──

    [Test]
    public async Task An_interactive_launch_succeeds_with_every_reviewer_gate_closed() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The interactive launch itself is exercised on POSIX.");
        var process = new FakePiRpcProcess();
        var factory = Factory(ConfigWithState(reviewerEnabled: false), process, installed: "0.1.0", posix: false);

        var start = await factory.StartAsync(Ctx(), CancellationToken.None);

        await Assert.That(start.Runtime).IsNotNull();
        await start.Runtime.DisposeAsync();
    }

    // ── validation happens before any directory exists ──

    [Test]
    [Arguments("serverUrl")]
    [Arguments("capacitorPath")]
    [Arguments("agentId")]
    public async Task A_blank_result_channel_input_refuses_before_a_launch_directory_exists(string blank) {
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");
        var ctx = Ctx(isReviewFlow: true) with {
            ServerUrl     = blank == "serverUrl"     ? " " : "http://kcap.test",
            CapacitorPath = blank == "capacitorPath" ? ""  : "/usr/local/bin/kcap",
            AgentId       = blank == "agentId"       ? ""  : "agent-1",
        };

        await Assert.That(async () => await Factory(config, new FakePiRpcProcess()).StartAsync(ctx, CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("pi_reviewer_launch_context_incomplete");
        await Assert.That(Directory.Exists(LaunchRoot(config))).IsFalse();
    }

    [Test]
    public async Task A_first_prompt_that_is_a_command_refuses_before_a_launch_directory_exists() {
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");
        var process = new FakePiRpcProcess();

        await Assert.That(async () => await Factory(config, process).StartAsync(Ctx(isReviewFlow: true, prompt: "/llama"), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("pi_reviewer_prompt_is_command");
        await Assert.That(Directory.Exists(LaunchRoot(config))).IsFalse();
        await Assert.That(process.Writes).IsEmpty();
    }

    [Test]
    public async Task A_rejected_allowlist_refuses_before_a_launch_directory_exists() {
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");
        var ctx = Ctx(isReviewFlow: true) with { McpAllowlist = ["kcap-flows"] };

        await Assert.That(async () => await Factory(config, new FakePiRpcProcess()).StartAsync(ctx, CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("pi_reviewer_allowlist_rejected");
        await Assert.That(Directory.Exists(LaunchRoot(config))).IsFalse();
    }

    // ── launch order, readiness and the pre-registration verdict ──

    [Test]
    public async Task The_first_prompt_is_written_only_after_the_tool_surface_is_confirmed() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess(AllFive);

        var start = await Factory(config, process).StartAsync(Ctx(isReviewFlow: true, prompt: "review this"), CancellationToken.None);

        var writes = process.Writes;
        await Assert.That(writes.ToList().FindIndex(w => w.Contains("\"type\":\"get_state\"")))
            .IsLessThan(writes.ToList().FindIndex(w => w.Contains("\"type\":\"prompt\"")));
        await start.Runtime.DisposeAsync();
    }

    [Test]
    [Arguments("""{"active":["read_file"]}""")]
    [Arguments("""{"active":["list_directory","read_file","search_files","send_flow_message","submit_review_result","bash"]}""")]
    public async Task A_tool_surface_mismatch_refuses_the_launch_and_sends_no_prompt(string ready) {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess(ready);

        await Assert.That(async () => await Factory(config, process).StartAsync(Ctx(isReviewFlow: true, prompt: "review this"), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("pi_reviewer_tool_surface_mismatch");

        await Assert.That(process.Writes.Any(w => w.Contains("\"type\":\"prompt\""))).IsFalse();
        await Assert.That(process.TerminateCalls + process.DisposeCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task A_child_that_never_answers_is_refused_as_an_extension_failure_without_its_stderr() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess(AllFive);
        process.AutoStateResponse = null;
        process.Diagnostics = "Failed to load extension: secret-looking text";

        var thrown = await Assert.That(async () => await Factory(config, process).StartAsync(Ctx(isReviewFlow: true), CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(thrown!.Message).StartsWith("pi_reviewer_extension_failed");
        await Assert.That(thrown.Message).DoesNotContain("secret-looking");
    }

    [Test]
    public async Task A_guard_that_wins_before_StartAsync_returns_throws_its_coded_reason() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess(AllFive);
        var ready = process.OnWrite!;
        process.OnWrite = json => {
            ready(json);
            if (json.Contains("\"type\":\"prompt\"")) process.Push(PiRpcRuntimeFakes.DialogRequest("confirm"));
        };

        // The dialog lands as the prompt is written. Either the factory's verdict read sees it and throws,
        // or the returned runtime already carries it; both are correct, neither is a healthy reviewer.
        try {
            var start = await Factory(config, process).StartAsync(Ctx(isReviewFlow: true, prompt: "review this"), CancellationToken.None);
            for (var i = 0; i < 200 && ((ITerminationVerdictSource)start.Runtime).ReadVerdict() is null; i++) await Task.Delay(10);
            await Assert.That(((ITerminationVerdictSource)start.Runtime).ReadVerdict()!.Reason).IsEqualTo("pi_reviewer_unexpected_dialog");
            await start.Runtime.DisposeAsync();
        } catch (PiReviewerReapedException ex) {
            await Assert.That(ex.Message).StartsWith("pi_reviewer_unexpected_dialog");
        }
    }

    [Test]
    public async Task The_launch_directory_is_removed_when_the_runtime_is_disposed() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess(AllFive);

        var start = await Factory(config, process).StartAsync(Ctx(isReviewFlow: true), CancellationToken.None);
        await Assert.That(Directory.EnumerateDirectories(LaunchRoot(config))).Count().IsEqualTo(1);

        await start.Runtime.DisposeAsync();

        await Assert.That(Directory.EnumerateDirectories(LaunchRoot(config))).IsEmpty();
    }

    [Test]
    public async Task The_launch_directory_is_removed_when_the_launch_fails() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var (config, process) = ReadyConfigAndProcess("""{"active":["read_file"]}""");

        await Assert.That(async () => await Factory(config, process).StartAsync(Ctx(isReviewFlow: true), CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(Directory.EnumerateDirectories(LaunchRoot(config))).IsEmpty();
    }

    [Test]
    public async Task The_launch_directory_is_removed_when_the_child_cannot_be_spawned() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only launch directory.");
        var config = ConfigWithState();
        PiRpcHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("0.85.1");
        var factory = new PiRpcHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance, TimeProvider.System,
            processSource: (_, _) => throw new IOException("spawn failed"),
            binaryExists: _ => true, resolveVersion: _ => "0.86.0", posixHost: true);

        await Assert.That(async () => await factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("pi_reviewer_extension_failed");

        await Assert.That(Directory.EnumerateDirectories(LaunchRoot(config))).IsEmpty();
    }
}
