using System.Runtime.Versioning;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Tests.Unit;

/// Shared vendor-detection fixture builder.
static class VendorDetection {
    public static IReadOnlySet<HarnessId> Build(params string[] detectedVendorIds) =>
        detectedVendorIds.Select(v => HarnessId.From(v)!.Value).ToHashSet();
}

/// The app's detection feed overrides the registry's search path with the login-shell terminal PATH
/// when the probe resolves one. Every registry here is hermetic, so nothing installed on this machine
/// (a CLI on PATH, a desktop app in /Applications) reaches a result. Pure static helper — no
/// AvaloniaSession needed.
public class AgentDetectionFeedTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Uses_the_probed_terminal_PATH_not_the_registry_PATH() {
        Skip.When(OperatingSystem.IsWindows(), "A shell-script stub only launches on POSIX.");

        var emptyDir  = Tmp.CreateDir("empty");
        var claudeDir = Tmp.CreateDir("claude");
        claudeDir.CreateExecutable("claude", "#!/bin/sh\n");

        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(emptyDir) };
        var withoutClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, TestHarnesses.Under(Home))(CancellationToken.None);

        probe.TerminalPathBehavior = _ => Task.FromResult<string?>(claudeDir);
        var withClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, TestHarnesses.Under(Home))(CancellationToken.None);

        await Assert.That(withoutClaude.ContainsKey(HarnessId.Claude)).IsFalse();
        await Assert.That(withClaude[HarnessId.Claude].BinaryFound).IsTrue();
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Falls_back_to_the_registry_PATH_when_the_probe_is_inconclusive() {
        Skip.When(OperatingSystem.IsWindows(), "A shell-script stub only launches on POSIX.");

        var bin = Tmp.CreateDir("bin");
        bin.CreateExecutable("claude", "#!/bin/sh\n");
        var binaries = BinaryProbe.Searching(bin);
        var probe    = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(null) };

        var actual = await HarnessesStepViewModel.BuildDetectionFeed(probe, TestHarnesses.Under(Home, binaries))(CancellationToken.None);

        await Assert.That(actual[HarnessId.Claude].BinaryFound).IsTrue();
    }

    /// Swapping in the terminal PATH must keep the registry's desktop-app roots: an agent installed
    /// only as an app is off every PATH.
    [Test]
    public async Task Finds_a_desktop_app_off_the_probed_terminal_PATH() {
        var apps = Tmp.CreateDir("apps");
        apps.CreateDir("Claude.app");
        var registry = HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(apps.Path),
            ClaudeHarness.Over(new ClaudePaths(Home, null)));
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(Tmp.CreateDir("empty")) };

        var actual = await HarnessesStepViewModel.BuildDetectionFeed(probe, registry)(CancellationToken.None);

        await Assert.That(actual[HarnessId.Claude]).IsEqualTo(new DetectedAgent(BinaryFound: false, InstallSignalFound: false, DesktopAppFound: true));
    }
}
