using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Tests.Unit;

/// Shared vendor-detection fixture builder.
static class VendorDetection {
    public static IReadOnlySet<HarnessId> Build(params string[] detectedVendorIds) =>
        detectedVendorIds.Select(v => HarnessId.From(v)!.Value).ToHashSet();
}

/// The app's detection feed overrides the process PATH with the login-shell terminal PATH when the
/// probe resolves one. Pure static helper — no AvaloniaSession needed.
public class AgentDetectionFeedTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Uses_the_probed_terminal_PATH_not_the_process_PATH() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based executable probe is POSIX-only.");

        using var tmp = new TempDir();
        var emptyDir  = tmp.CreateDir("empty");
        var claudeDir = tmp.CreateDir("claude");
        tmp.CreateExecutable("claude/claude", "#!/bin/sh\n");

        // Same probe, two different crafted PATHs: the outcome must track the probe, not
        // whatever the real process PATH happens to contain on this machine.
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(emptyDir) };
        var withoutClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        probe.TerminalPathBehavior = _ => Task.FromResult<string?>(claudeDir);
        var withClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        // Only the launch signal follows PATH: the desktop-app probe always searches /Applications,
        // so a Mac with Claude.app still lists the row.
        await Assert.That(withoutClaude.GetValueOrDefault(HarnessId.Claude)?.BinaryFound ?? false).IsFalse();
        await Assert.That(withClaude[HarnessId.Claude].BinaryFound).IsTrue();
    }

    [Test]
    public async Task Falls_back_to_the_process_PATH_when_the_probe_is_inconclusive() {
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(null) };

        var actual = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        // Pins the process-PATH fallback, so expected must come from the same resolution
        // BuildDetectionFeed falls back to, not a hermetic registry.
        var harnesses = HarnessRegistry.FromEnvironment(Home);
        var expected  = harnesses.Where(h => harnesses.Detected(h.Id)).Select(h => h.Id);

        await Assert.That(actual.Keys).IsEquivalentTo(expected);
    }
}
