using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>The codex capability is read from the committed resolution the router also reads, and no
/// other vendor advertises an interactive transport. The server records the advertised transport as a
/// launch's expected one, so an advertisement that disagreed with routing would refuse every launch.</summary>
public class DaemonRunnerInteractiveTransportAdvertisementTests {
    sealed class FakeFactory(string vendor, string cliPath = "") : IHostedAgentRuntimeFactory {
        public string CliPath            { get; } = cliPath;
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => true;

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    // A codex path that cannot run: were the advertisement to probe Codex itself, its version would
    // come back null instead of the resolution's.
    static IReadOnlyList<UnattendedVendorCapability> Advertised(CodexTransportResolution codex) =>
        DaemonRunner.ComputeUnattendedVendorCapabilities(
            [new FakeFactory("codex", "/definitely/missing/codex"), new FakeFactory("claude")], new DaemonConfig(), codex,
            advertised: ["codex", "claude"]);

    [Test]
    [Arguments("0.146.0")]
    [Arguments("0.145.0")]
    public async Task Codex_advertises_the_committed_resolution(string version) {
        var resolution = CodexTransportResolution.Resolve(4, switchOn: true, () => version);

        var codex = Advertised(resolution).Single(c => c.Vendor == "codex");

        await Assert.That(codex.CliVersion).IsEqualTo(version);
        await Assert.That(codex.LauncherPolicyVersion).IsEqualTo(resolution.LauncherPolicyVersion);
        await Assert.That(codex.InteractiveTransport).IsEqualTo(resolution.InteractiveTransport);
    }

    [Test]
    public async Task Other_vendors_advertise_no_interactive_transport() {
        var claude = Advertised(CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0")).Single(c => c.Vendor == "claude");

        await Assert.That(claude.InteractiveTransport).IsNull();
    }
}
