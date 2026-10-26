using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.RetainAdvertisedVersions"/>: a re-probe that fails must not replace a
/// version already advertised. The server reads a null version as the vendor being gone, so
/// publishing one over a transient probe miss would withdraw the reviewer.
/// </summary>
public class DaemonRunnerCapabilityRefreshTests {
    /// <summary>Every path here is rooted, which resolves without a search path.</summary>
    static BinaryProbe Binaries => TestBinaries.None;

    static readonly CodexTransportResolution SwitchOff = CodexTransportResolution.Resolve(1, switchOn: false, () => null);

    static UnattendedVendorCapability Cap(string vendor, string? version, bool borrowed = false) =>
        new(vendor, version, $"{vendor}-unattended-v1", borrowed);

    [Test]
    public async Task A_failed_reprobe_keeps_the_previously_advertised_version() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", "2.1.259")], [Cap("claude", null)], SwitchOff);

        await Assert.That(merged.Single().CliVersion).IsEqualTo("2.1.259");
    }

    [Test]
    public async Task A_successful_reprobe_replaces_the_advertised_version() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", "2.1.259")], [Cap("claude", "2.1.263")], SwitchOff);

        await Assert.That(merged.Single().CliVersion).IsEqualTo("2.1.263");
    }

    [Test]
    public async Task A_vendor_never_advertised_with_a_version_stays_unversioned() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", null)], [Cap("claude", null)], SwitchOff);

        await Assert.That(merged.Single().CliVersion).IsNull();
    }

    [Test]
    public async Task A_vendor_absent_from_the_reprobe_is_dropped() {
        var merged = DaemonRunner.RetainAdvertisedVersions(
            [Cap("claude", "2.1.259"), Cap("codex", "0.153.0")], [Cap("claude", "2.1.263")], SwitchOff);

        await Assert.That(merged.Select(c => c.Vendor)).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task With_nothing_advertised_yet_the_reprobe_is_returned_unchanged() {
        var fresh  = new[] { Cap("claude", null), Cap("codex", "0.153.0") };
        var merged = DaemonRunner.RetainAdvertisedVersions(null, fresh, SwitchOff);

        await Assert.That(merged).IsEquivalentTo(fresh);
    }

    [Test]
    public async Task Every_field_but_the_version_comes_from_the_reprobe() {
        var merged = DaemonRunner.RetainAdvertisedVersions(
            [Cap("codex", "0.153.0", borrowed: false)], [Cap("codex", null, borrowed: true)], SwitchOff);

        await Assert.That(merged.Single()).IsEqualTo(Cap("codex", "0.153.0", borrowed: true));
    }

    /// <summary>With the switch on, Codex's version is the resolution's, probed together with the policy and
    /// transport published beside it; keeping an older one would pair it with a policy it did not produce.
    /// Other vendors, and Codex with the switch off, keep the advertised version over a probe miss.</summary>
    [Test]
    [Arguments(true,  "codex",  null)]
    [Arguments(false, "codex",  "0.146.0")]
    [Arguments(true,  "claude", "0.146.0")]
    public async Task An_unreadable_codex_resolution_is_published_as_is(bool switchOn, string vendor, string? published) {
        var codex  = CodexTransportResolution.Resolve(2, switchOn, () => null);
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap(vendor, "0.146.0")], [Cap(vendor, null)], codex);

        await Assert.That(merged.Single().CliVersion).IsEqualTo(published);
    }

    static PtyHostedAgentRuntimeFactory Factory(string vendor, string cliPath) =>
        new(new SpyHostedAgentLauncher(vendor, cliPath), new SpyPtyProcessFactory(),
            NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, home: new UserHome("/unused-home"));

    // The startup fingerprint is what the watcher compares against, so it must describe the same
    // file the version probe ran, through the same factory-owned path.
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Startup_fingerprints_describe_each_advertised_vendors_binary() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        using var tmp = new TempDir();
        var claude = tmp.CreateFile("claude", "#!/bin/sh\necho 2.1.263\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var baselines = DaemonRunner.FingerprintUnattendedVendors(
            Binaries, [Factory("claude", claude), Factory("codex", tmp.PathTo("missing-codex"))],
            ["claude", "codex"]);

        await Assert.That(baselines["claude"]!.Value.ResolvedPath).IsEqualTo(new FileInfo(claude).FullName);
        await Assert.That(baselines["codex"]).IsNull();
    }
}
