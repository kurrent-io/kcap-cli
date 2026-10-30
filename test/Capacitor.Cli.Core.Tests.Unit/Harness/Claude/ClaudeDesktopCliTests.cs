using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// <summary>Which of the desktop app's downloaded Claude Code builds is launched. The layout is the
/// macOS app's, whose executable carries no extension, so Windows cannot launch it and skips.</summary>
public class ClaudeDesktopCliTests {
    [TempDir] public required TempDir Apps { get; init; }

    DesktopAppProbe Probe => DesktopAppProbe.Searching(Apps.Path);

    string StageBuild(string version, string build, bool verified = true) {
        var exe = Apps.CreateExecutable(
            Path.Combine("Claude", "claude-code", version, build, "claude.app", "Contents", "MacOS", "claude"), "#!/bin/sh\n");
        if (verified) Apps.CreateFile(Path.Combine("Claude", "claude-code", version, build, ".verified"));

        return exe;
    }

    [Before(Test)]
    public void NotOnWindows() => Skip.When(OperatingSystem.IsWindows(), "macOS bundle layout");

    [Test]
    public async Task Finds_the_newest_version_by_number_not_by_name() {
        StageBuild("2.1.99", "aaaa");
        var newest = StageBuild("2.1.309", "bbbb");

        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsEqualTo(newest);
    }

    /// <summary>The newer build is mid-download, so the older verified one is what runs.</summary>
    [Test]
    public async Task Skips_a_build_the_app_has_not_verified() {
        var verified = StageBuild("2.1.308", "aaaa");
        StageBuild("2.1.309", "bbbb", verified: false);

        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsEqualTo(verified);
    }

    [Test]
    public async Task Skips_a_verified_build_whose_executable_is_missing() {
        var older = StageBuild("2.1.308", "aaaa");
        Apps.CreateFile(Path.Combine("Claude", "claude-code", "2.1.309", "bbbb", ".verified"));

        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsEqualTo(older);
    }

    [Test]
    public async Task Ignores_a_directory_that_is_not_a_version() {
        var real = StageBuild("2.1.308", "aaaa");
        StageBuild("staging", "bbbb");

        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsEqualTo(real);
    }

    [Test]
    public async Task A_release_outranks_its_own_pre_release() {
        StageBuild("2.1.309-beta", "aaaa");
        var release = StageBuild("2.1.309", "bbbb");

        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsEqualTo(release);
    }

    [Test]
    public async Task Finds_nothing_without_the_app_data() {
        await Assert.That(ClaudeDesktopCli.Find(Probe)).IsNull();
        await Assert.That(ClaudeDesktopCli.Find(DesktopAppProbe.None)).IsNull();
    }
}
