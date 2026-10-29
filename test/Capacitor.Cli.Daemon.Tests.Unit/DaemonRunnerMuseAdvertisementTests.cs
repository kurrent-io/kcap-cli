using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>What a daemon says about Muse Code once its factory is registered: the unattended list,
/// the withheld reason an operator can act on, and the capability the server routes a review on.</summary>
public class DaemonRunnerMuseAdvertisementTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempHome]        public required TempHome        Home { get; init; }

    DaemonConfig Config() {
        var config = new DaemonConfig {
            Name                          = "test-daemon",
            ServerUrl                     = "http://127.0.0.1:1",
            MusePath                      = "/opt/muse/bin/muse",
            MuseUnattendedReviewerEnabled = true,
            Store                         = Daemons.Store,
            Binaries                      = TestBinaries.None,
        };

        MuseHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("1.4.7");
        return config;
    }

    MuseHostedAgentRuntimeFactory Factory(DaemonConfig config, bool binary) =>
        new(config, NullLoggerFactory.Instance, TimeProvider.System,
            new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
                NullLogger<ServerConnection>.Instance, TimeProvider.System),
            new MusePaths(Home, null, null),
            binaryExists: _ => binary,
            resolveVersion: _ => "1.4.7",
            posixHost: true,
            sandbox: new MuseSandboxProbe(() => true));

    [Test]
    public async Task An_installed_affirmed_muse_is_advertised_as_an_unattended_reviewer() {
        var statuses = DaemonRunner.ClassifyUnattendedVendors([Factory(Config(), binary: true)]);

        await Assert.That(DaemonRunner.AdvertisedUnattendedVendors(statuses)).IsEquivalentTo(new[] { "muse" });
    }

    [Test]
    public async Task A_missing_binary_is_not_installed_and_so_not_advertised() {
        var factory  = Factory(Config(), binary: false);
        var statuses = DaemonRunner.ClassifyUnattendedVendors([factory]);

        await Assert.That(statuses).IsEmpty();
        await Assert.That(factory.DescribeUnattendedSupport().WithheldReason!).StartsWith("muse_reviewer_binary_missing");
    }

    [Test]
    public async Task The_capability_carries_the_muse_policy_version_and_snapshot_containment() {
        var config  = Config();
        var factory = Factory(config, binary: true);

        var capability = DaemonRunner.ComputeUnattendedVendorCapabilities([factory], config, CodexTransportResolution.Unresolved).Single();

        await Assert.That(capability.Vendor).IsEqualTo("muse");
        await Assert.That(capability.LauncherPolicyVersion).IsEqualTo("muse-unattended-v1");
        await Assert.That(capability.BorrowedReviewContainment).IsEqualTo("independent-snapshot");
    }
}
