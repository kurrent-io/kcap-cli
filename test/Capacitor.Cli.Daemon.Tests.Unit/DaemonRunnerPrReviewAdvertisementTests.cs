using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonRunnerPrReviewAdvertisementTests {
    [TempHome] public required TempHome Home { get; init; }

    sealed class FakeRuntimeFactory(string vendor, bool isAvailable, bool supportsPrReview) : IHostedAgentRuntimeFactory {
        public string CliPath            => "unused-by-this-double";
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;
        public bool   SupportsPrReview   { get; } = supportsPrReview;

        public bool IsAvailable() => isAvailable;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    static DaemonConfig LauncherCfg() => new() { Name = "t", ServerUrl = "http://127.0.0.1:1" };

    [Test]
    public async Task Advertises_only_installed_runtimes_that_host_a_pr_review() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("codex", isAvailable: true, supportsPrReview: true),
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsPrReview: false),
            new FakeRuntimeFactory("claude", isAvailable: true, supportsPrReview: true),
            new FakeRuntimeFactory("gemini", isAvailable: false, supportsPrReview: true),
        ];

        await Assert.That(DaemonRunner.AdvertisedPrReviewVendors(factories)).IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task Claude_and_codex_launchers_host_a_pr_review() {
        IHostedAgentLauncher claude = new ClaudeLauncher(LauncherCfg(), TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        IHostedAgentLauncher codex  = new CodexLauncher(LauncherCfg(), TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance);

        await Assert.That(claude.SupportsPrReview).IsTrue();
        await Assert.That(codex.SupportsPrReview).IsTrue();
    }

    [Test]
    public async Task Acp_runtimes_do_not_host_a_pr_review() {
        var connection = new ServerConnection(
            LauncherCfg(), UnusedTokenStore.Create(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        IHostedAgentRuntimeFactory cursor = new AcpHostedAgentRuntimeFactory(
            AcpVendorDescriptors.Cursor, new DaemonConfig { CursorPath = "cursor-agent" },
            NullLoggerFactory.Instance, connection, TimeProvider.System);

        await Assert.That(cursor.SupportsPrReview).IsFalse();
    }
}
