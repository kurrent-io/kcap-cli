using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>A machine with only the ChatGPT desktop app: no <c>codex</c> on PATH, so the launcher
/// spawns the CLI the app bundles.</summary>
public class CodexLauncherBundledCliTests {
    [TempDir] public required TempDir Apps { get; init; }

    CodexLauncher Launcher() =>
        new(new DaemonConfig { Name = "t", ServerUrl = "http://127.0.0.1:1", Binaries = TestBinaries.None },
            HarnessRegistry.Over(TestBinaries.None, DesktopAppProbe.Searching(Apps.Path),
                CodexHarness.Over(new CodexPaths(new UserHome("/nonexistent-home"), null))),
            NullLogger<CodexLauncher>.Instance);

    [Before(Test)]
    public void NotOnWindows() => Skip.When(OperatingSystem.IsWindows(), "macOS bundle layout");

    [Test]
    public async Task Launches_and_advertises_the_chatgpt_apps_codex() {
        var exe = Apps.CreateExecutable(
            Path.Combine("ChatGPT.app", "Contents", "Resources", "codex-cli", "bin", "codex"), "#!/bin/sh\n");
        var launcher = Launcher();

        await Assert.That(launcher.CliPath).IsEqualTo(exe);
        await Assert.That(launcher.IsAvailable()).IsTrue();
    }

    [Test]
    public async Task Is_unavailable_without_the_app() {
        var launcher = Launcher();

        await Assert.That(launcher.CliPath).IsEqualTo(CodexHarness.CliBinary);
        await Assert.That(launcher.IsAvailable()).IsFalse();
    }
}
