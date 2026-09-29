using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="AgentOrchestrator.RefreshAdvertisedCapabilities"/>: the one path through which a
/// running daemon re-advertises its vendor CLI versions, shared by the binary watcher and the
/// certification rejection. Re-probes the real stub binary rather than faking the probe, so what is
/// pinned is the advertisement the server would see.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorCapabilityRefreshTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string MissingCli = "/definitely/missing/claude";

    string StubClaude(string version) =>
        Tmp.CreateExecutable("claude", $"#!/bin/sh\necho '{version} (Claude Code)'\n");

    static UnattendedVendorCapability Advertised(string? version) =>
        new("claude", version, DaemonRunner.ClaudeLauncherPolicyVersion, false);

    static (AgentOrchestrator Orchestrator, CaptureServerConnection Server, DaemonConfig Config) Build(
            string cliPath, IReadOnlyList<UnattendedVendorCapability>? advertised) {
        var server   = new CaptureServerConnection();
        var launcher = new SpyHostedAgentLauncher("claude", cliPath) { SupportsUnattended = true };
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = launcher },
            configure: config => {
                config.UnattendedVendors             = ["claude"];
                config.UnattendedVendorCapabilities  = advertised;
                captured = config;
            });
        return (orch, server, captured!);
    }

    // No unattended vendors, so the capability half reads unchanged and only the catalog decides.
    static (AgentOrchestrator Orchestrator, CaptureServerConnection Server, DaemonConfig Config) BuildWithCatalog(
            StubCatalogFactory factory, Dictionary<string, VendorModelOption[]>? initial) {
        var server = new CaptureServerConnection();
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [factory],
            configure: config => {
                config.UnattendedVendors            = [];
                config.UnattendedVendorCapabilities = [];
                config.VendorModels                 = initial;
                captured = config;
            });
        return (orch, server, captured!);
    }

    static Dictionary<string, VendorModelOption[]> Catalog(params VendorModelOption[] pi) =>
        new(StringComparer.Ordinal) { ["pi"] = pi };

    static string? ClaudeVersion(DaemonConfig config) =>
        config.UnattendedVendorCapabilities!.Single(c => c.Vendor == "claude").CliVersion;

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_changed_binary_is_re_advertised_with_its_installed_version() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var (orch, server, config) = Build(StubClaude("2.1.263"), [Advertised("2.1.259")]);
        await using var _ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ClaudeVersion(config)).IsEqualTo("2.1.263");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unchanged_advertisement_is_not_re_registered() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var (orch, server, _) = Build(StubClaude("2.1.263"), advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;
        orch.RefreshAdvertisedCapabilities("second");
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_reprobe_keeps_the_advertised_version() {
        var (orch, _, config) = Build(MissingCli, [Advertised("2.1.259")]);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ClaudeVersion(config)).IsEqualTo("2.1.259");
    }

    // A certification rejection means the server's copy disagrees with the installed binary, so
    // that path republishes even when the local advertisement already reads the same.
    [Test]
    public async Task A_republish_request_re_registers_an_unchanged_advertisement() {
        var (orch, server, _) = Build(MissingCli, advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;
        orch.RefreshAdvertisedCapabilities("rejection", republishUnchanged: true);
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(2);
    }

    // The refresh is single-flighted, so a rejection's republish request that lands while a
    // watcher-triggered pass is running is folded into that pass or its rerun. Folding must not
    // drop the republish, or the retry the rejection promised meets the same stale server copy.
    [Test]
    public async Task A_republish_request_folded_into_a_running_pass_still_re_registers() {
        var (orch, server, _) = Build(MissingCli, advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;

        orch.RefreshAdvertisedCapabilities("watcher");
        orch.RefreshAdvertisedCapabilities("rejection", republishUnchanged: true);
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(2);
    }

    [Test]
    public async Task A_changed_catalog_is_republished_by_reference_swap_and_pulses_status() {
        var (orch, server, config) = BuildWithCatalog(new StubCatalogFactory("pi", [new("p/a", "A · p")]), Catalog());
        await using var _ = orch;
        var before = config.VendorModels;
        var v0     = orch.StatusNotifierForTest.Version;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ReferenceEquals(config.VendorModels, before)).IsFalse();
        await Assert.That(config.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
        await Assert.That(before!["pi"]).IsEmpty();
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);
    }

    [Test]
    public async Task An_equal_content_reprobe_neither_republishes_nor_swaps() {
        var (orch, server, config) = BuildWithCatalog(new StubCatalogFactory("pi", [new("p/a", "A · p")]), Catalog(new VendorModelOption("p/a", "A · p")));
        await using var _ = orch;
        var before = config.VendorModels;
        var v0     = orch.StatusNotifierForTest.Version;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ReferenceEquals(config.VendorModels, before)).IsTrue();
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0);
    }

    [Test]
    public async Task A_null_reprobe_keeps_the_previous_entry() {
        var stub = new StubCatalogFactory("pi", null);
        var (orch, server, config) = BuildWithCatalog(stub, Catalog(new VendorModelOption("p/a", "A · p")));
        await using var _ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(stub.ProbeCalls).IsEqualTo(1);
        await Assert.That(config.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_catalog_producer_with_fingerprint_paths_is_probed_before_it_has_an_entry() {
        var stub = new StubCatalogFactory("pi", [new("p/a", "A · p")], paths: ["/nonexistent/auth.json"]);
        var (orch, server, config) = BuildWithCatalog(stub, initial: null);
        await using var _ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(config.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    /// A reader takes the reference once, as the status IPC does; a refresh publishing meanwhile
    /// must never hand it a dictionary that changes under enumeration.
    [Test]
    public async Task A_status_serializer_racing_a_refresh_sees_a_whole_dictionary() {
        var models = Enumerable.Range(0, 2000).Select(i => new VendorModelOption($"p/{i}", $"{i} · p")).ToArray();
        var (orch, _, config) = BuildWithCatalog(new StubCatalogFactory("pi", models), Catalog());
        await using var _ = orch;

        var serializing = Task.Run(() => {
            for (var i = 0; i < 200; i++) {
                var snapshot = config.VendorModels;
                var json     = JsonSerializer.Serialize(new DaemonInfoDto("d", "1", "s", "connected", 1, 0, VendorModels: snapshot),
                    StatusIpcJsonContext.Default.DaemonInfoDto);
                var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonInfoDto)!;
                if (back.VendorModels!["pi"].Length is not (0 or 2000))
                    throw new InvalidOperationException($"partial catalog: {back.VendorModels["pi"].Length}");
            }
        });
        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;
        await serializing;

        await Assert.That(config.VendorModels!["pi"].Length).IsEqualTo(2000);
    }
}
