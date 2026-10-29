using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The capability refresh resolves the <c>codex-app-server</c> switch against the installed Codex
/// and commits one resolution that routing, the advertisement and status all read. Probes a real
/// stub binary, so the version is the one the daemon would advertise.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
[UnsupportedOSPlatform("windows")]
public class AgentOrchestratorCodexResolutionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string StubCodex(string name, string version, string before = "") =>
        Tmp.CreateExecutable(name, $"#!/bin/sh\n{before}echo 'codex-cli {version}'\n");

    sealed class OrchestratorLog(CapturingLogger inner) : ILogger<AgentOrchestrator> {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
            inner.Log(level, id, state, ex, format);
    }

    static (AgentOrchestrator Orchestrator, CaptureServerConnection Server, DaemonConfig Config) Build(
            string codexPath, Exception? registerThrow = null, CapturingLogger? log = null) {
        var config   = new DaemonConfig();
        var server   = new CaptureServerConnection(config) { RegisterDaemonThrow = registerThrow };
        var launcher = new SpyHostedAgentLauncher("codex", codexPath) { SupportsUnattended = true };
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["codex"] = launcher },
            configure: c => {
                c.CodexPath         = codexPath;
                c.UnattendedVendors = ["codex"];
            },
            sharedConfig: config,
            logger: log is null ? null : new OrchestratorLog(log));
        return (orch, server, config);
    }

    static Task SwitchAsync(DaemonConfig config, bool on) =>
        new ExperimentalSettingsStore(config.ConfigRoot)
            .SetAsync(config.Profiles.Name, ExperimentalFeatures.CodexAppServer.Id, on, CancellationToken.None);

    static async Task RefreshAsync(AgentOrchestrator orch) {
        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;
    }

    static async Task RefreshTolerantAsync(AgentOrchestrator orch) {
        orch.RefreshAdvertisedCapabilities("test");
        try { await orch.CapabilityRefreshForTest; } catch (InvalidOperationException) { }
    }

    static UnattendedVendorCapability Codex(DaemonConfig config) =>
        config.UnattendedVendorCapabilities!.Single(c => c.Vendor == "codex");

    static void SkipOnWindows() => Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");

    [Test]
    public async Task Refresh_commits_one_generation_for_routing_advertisement_and_status() {
        SkipOnWindows();
        var (orch, server, config) = Build(StubCodex("codex", "0.146.0"));
        await using var _ = orch;
        await SwitchAsync(config, on: true);

        await RefreshAsync(orch);

        var committed = config.CodexResolution;
        await Assert.That(committed).IsEqualTo(new CodexTransportResolution(1, true, "0.146.0", true, null));
        await Assert.That(Codex(config).CliVersion).IsEqualTo(committed.CliVersion);
        await Assert.That(Codex(config).LauncherPolicyVersion).IsEqualTo(committed.LauncherPolicyVersion);
        await Assert.That(Codex(config).InteractiveTransport).IsEqualTo(committed.InteractiveTransport);
        await Assert.That(ReferenceEquals(config.AcknowledgedCodexResolution, committed)).IsTrue();
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    /// <summary>The stub turns the switch off while it is being probed; without a re-read the pass
    /// would commit an app-server resolution for a switch that is already off.</summary>
    [Test]
    public async Task Refresh_reruns_when_the_switch_flips_during_the_probe() {
        SkipOnWindows();
        var (orch, _, config) = Build(StubCodex("codex", "0.146.0"));
        await using var _ = orch;
        await SwitchAsync(config, on: true);
        var store = new ExperimentalSettingsStore(config.ConfigRoot);
        var off   = Tmp.CreateFile("off.json", $$$"""{"{{{config.Profiles.Name}}}":{"codex-app-server":false}}""");
        config.CodexPath = StubCodex("codex-flipping", "0.146.0", before: $"cp '{off}' '{store.Path}'\n");

        await RefreshAsync(orch);

        await Assert.That(config.CodexResolution.SwitchOn).IsFalse();
        await Assert.That(config.CodexResolution.Active).IsFalse();
        await Assert.That(Codex(config).InteractiveTransport).IsEqualTo(CodexTransportDecision.Pty);
    }

    /// <summary>The server refuses every registration here, so a pulse can only come from the commit.</summary>
    [Test]
    public async Task Refresh_pulses_status_when_only_the_resolution_changed() {
        SkipOnWindows();
        var (orch, _, config) = Build(StubCodex("codex", "0.145.0"), registerThrow: new InvalidOperationException("hub down"));
        await using var _ = orch;
        await RefreshTolerantAsync(orch);
        var advertised = Codex(config);
        var v0         = orch.StatusNotifierForTest.Version;

        await SwitchAsync(config, on: true);
        await RefreshTolerantAsync(orch);

        await Assert.That(Codex(config)).IsEqualTo(advertised);
        await Assert.That(config.CodexResolution.SwitchOn).IsTrue();
        await Assert.That(config.CodexResolution.InactiveReason).IsEqualTo("Codex 0.145.0 is below 0.146.0");
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);
    }

    /// <summary>A half-written or hand-broken file says nothing about the switch, so it must not turn an
    /// active transport off; the problem is logged once until it changes.</summary>
    [Test]
    public async Task An_unreadable_store_keeps_the_committed_resolution() {
        SkipOnWindows();
        var log = new CapturingLogger();
        var (orch, _, config) = Build(StubCodex("codex", "0.146.0"), log: log);
        await using var _ = orch;
        await SwitchAsync(config, on: true);
        await RefreshAsync(orch);
        var committed = config.CodexResolution;
        var store     = new ExperimentalSettingsStore(config.ConfigRoot);

        await File.WriteAllTextAsync(store.Path, "{ not json");
        await RefreshAsync(orch);
        await RefreshAsync(orch);

        await Assert.That(ReferenceEquals(config.CodexResolution, committed)).IsTrue();
        await Assert.That(log.Warnings.Count(w => w.Contains("not valid JSON"))).IsEqualTo(1);
        await Assert.That(config.ExperimentalSettingsProblem).Contains("not valid JSON");

        await File.WriteAllTextAsync(store.Path, "[1]");
        await RefreshAsync(orch);

        await Assert.That(ReferenceEquals(config.CodexResolution, committed)).IsTrue();
        await Assert.That(log.Warnings.Count(w => w.Contains("not a JSON object"))).IsEqualTo(1);

        File.Delete(store.Path);
        await RefreshAsync(orch);

        await Assert.That(config.CodexResolution.SwitchOn).IsFalse();
        await Assert.That(config.ExperimentalSettingsProblem).IsNull();
    }

    [Test]
    public async Task Failed_reregistration_keeps_the_commit_and_leaves_acknowledgement_behind() {
        SkipOnWindows();
        var (orch, _, config) = Build(StubCodex("codex", "0.146.0"), registerThrow: new InvalidOperationException("hub down"));
        await using var _ = orch;
        await SwitchAsync(config, on: true);

        await RefreshTolerantAsync(orch);

        await Assert.That(config.CodexResolution.Active).IsTrue();
        await Assert.That(config.CodexResolution.Generation).IsGreaterThan(config.AcknowledgedCodexResolution.Generation);
        await Assert.That(config.FailedCodexGeneration).IsEqualTo(config.CodexResolution.Generation);
    }

    /// <summary>The refresh's own registration fails; a later one from another path (here the
    /// heartbeat's) carries the same advertisement, so it must acknowledge the resolution behind it.</summary>
    [Test]
    public async Task A_later_registration_from_another_path_acknowledges_the_committed_resolution() {
        SkipOnWindows();
        var (orch, server, config) = Build(StubCodex("codex", "0.146.0"), registerThrow: new InvalidOperationException("hub down"));
        await using var _ = orch;
        await SwitchAsync(config, on: true);
        await RefreshTolerantAsync(orch);
        await Assert.That(config.AcknowledgedCodexResolution.Generation).IsLessThan(config.CodexResolution.Generation);

        server.RegisterDaemonThrow = null;
        await server.ReRegisterAsync();

        await Assert.That(ReferenceEquals(config.AcknowledgedCodexResolution, config.CodexResolution)).IsTrue();
    }

    [Test]
    public async Task An_older_registration_finishing_last_does_not_move_the_acknowledgement_back() {
        var config = new DaemonConfig();
        var newer  = new CodexTransportResolution(3, true, "0.146.0", true, null);

        await Assert.That(config.AcknowledgeCodex(newer)).IsTrue();
        await Assert.That(config.AcknowledgeCodex(newer with { Generation = 2 })).IsFalse();
        await Assert.That(ReferenceEquals(config.AcknowledgedCodexResolution, newer)).IsTrue();
    }

    [Test]
    public async Task Codex_upgrade_past_the_floor_activates_a_switched_on_feature() {
        SkipOnWindows();
        var (orch, _, config) = Build(StubCodex("codex", "0.145.0"));
        await using var _ = orch;
        await SwitchAsync(config, on: true);
        await RefreshAsync(orch);
        await Assert.That(config.CodexResolution.Active).IsFalse();

        config.CodexPath = StubCodex("codex-upgraded", "0.146.0");
        await RefreshAsync(orch);

        await Assert.That(config.CodexResolution).IsEqualTo(new CodexTransportResolution(2, true, "0.146.0", true, null));
        await Assert.That(Codex(config).LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexAppServerLauncherPolicyVersion);
        await Assert.That(config.AcknowledgedCodexResolution.Generation).IsEqualTo(2L);
    }
}
