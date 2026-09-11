using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>The codex capability advertises the interactive transport the router will actually use, and
/// no other vendor advertises one. The server records the advertised value as the launch's expected
/// transport, so an advertisement that disagreed with routing would refuse every interactive launch.</summary>
public class DaemonRunnerInteractiveTransportAdvertisementTests {
    sealed class FakeFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string CliPath            => "";   // empty: no version probe is spawned
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => true;

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    static IReadOnlyList<UnattendedVendorCapability> Advertised(DaemonConfig config) =>
        DaemonRunner.ComputeUnattendedVendorCapabilities(
            [new FakeFactory("codex"), new FakeFactory("claude")], config, advertised: ["codex", "claude"]);

    [Test]
    [Arguments(false, false)]
    [Arguments(true,  false)]
    [Arguments(false, true)]
    [Arguments(true,  true)]
    public async Task Codex_advertises_the_interactive_transport_the_router_uses(bool active, bool optIn) {
        var config = new DaemonConfig { CodexAppServerActive = active, CodexAppServerInteractive = optIn };

        var codex = Advertised(config).Single(c => c.Vendor == "codex");

        await Assert.That(codex.InteractiveTransport).IsEqualTo(CodexTransportDecision.InteractiveTransport(config));
    }

    [Test]
    public async Task Other_vendors_advertise_no_interactive_transport() {
        var config = new DaemonConfig { CodexAppServerActive = true, CodexAppServerInteractive = true };

        var claude = Advertised(config).Single(c => c.Vendor == "claude");

        await Assert.That(claude.InteractiveTransport).IsNull();
    }
}
