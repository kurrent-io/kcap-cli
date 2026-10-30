using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>A machine with only the Claude desktop app: no <c>claude</c> on PATH, so the launcher
/// spawns the build the app downloaded.</summary>
public class ClaudeLauncherBundledCliTests {
    [TempDir] public required TempDir Apps { get; init; }

    ClaudeLauncher Launcher(string claudePath) =>
        new(new DaemonConfig { Name = "t", ServerUrl = "http://127.0.0.1:1", Binaries = TestBinaries.None, ClaudePath = claudePath },
            HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(Apps.Path),
                ClaudeHarness.Over(new ClaudePaths(new UserHome("/nonexistent-home"), null))),
            NullLogger<ClaudeLauncher>.Instance);

    string StageBuild(string version) {
        var dir = Path.Combine("Claude", "claude-code", version, "abc");
        Apps.CreateFile(Path.Combine(dir, ".verified"));

        return Apps.CreateExecutable(Path.Combine(dir, "claude.app", "Contents", "MacOS", "claude"), "#!/bin/sh\n");
    }

    [Before(Test)]
    public void NotOnWindows() => Skip.When(OperatingSystem.IsWindows(), "macOS bundle layout");

    [Test]
    public async Task Launches_and_advertises_the_desktop_apps_claude() {
        var exe      = StageBuild("2.1.309");
        var launcher = Launcher(ClaudeHarness.CliBinary);

        await Assert.That(launcher.CliPath).IsEqualTo(exe);
        await Assert.That(launcher.IsAvailable()).IsTrue();
    }

    /// <summary>The app updated after the daemon started: the next launch takes the new build.</summary>
    [Test]
    public async Task Picks_up_a_build_installed_after_the_launcher_was_built() {
        StageBuild("2.1.308");
        var launcher = Launcher(ClaudeHarness.CliBinary);
        var newer    = StageBuild("2.1.309");

        await Assert.That(launcher.CliPath).IsEqualTo(newer);
    }

    [Test]
    public async Task Leaves_a_configured_path_alone() {
        StageBuild("2.1.309");
        var launcher = Launcher("/opt/claude/claude");

        await Assert.That(launcher.CliPath).IsEqualTo("/opt/claude/claude");
        await Assert.That(launcher.IsAvailable()).IsFalse();
    }

    [Test]
    public async Task Is_unavailable_without_the_app() {
        var launcher = Launcher(ClaudeHarness.CliBinary);

        await Assert.That(launcher.CliPath).IsEqualTo(ClaudeHarness.CliBinary);
        await Assert.That(launcher.IsAvailable()).IsFalse();
    }
}
