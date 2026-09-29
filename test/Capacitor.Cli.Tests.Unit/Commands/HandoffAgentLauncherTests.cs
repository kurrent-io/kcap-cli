using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Bare <c>[NotInParallel]</c>: <c>EnvScope.Exclusive</c> seeds <c>KCAP_URL</c> in the
/// process environment, which every spawned child inherits.</summary>
[NotInParallel]
public class HandoffAgentLauncherTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static HandoffLaunchRequest Request(string exe) => new(new HandoffVendor(HarnessId.Gemini, "Gemini", exe), "Follow my kcap import\n(run: x)", "work", "/tmp/wd");

    [Test]
    public async Task Psi_is_interactive_pinned_to_the_saved_profile_and_uses_the_recipe() {
        if (OperatingSystem.IsWindows()) return;

        using var _ = EnvScope.Exclusive(ProfileOverrides.UrlVar, "https://ambient.test");
        var starter = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("sleep", "3") { UseShellExecute = false }));

        var result = new HandoffAgentLauncher(Config.Root, starter).Launch(Request("/opt/bin/gemini"));

        var psi = starter.Seen!;
        await Assert.That(psi.FileName).IsEqualTo("/opt/bin/gemini");
        await Assert.That(psi.ArgumentList.ToList()).IsEquivalentTo(["-i", "Follow my kcap import\n(run: x)"]);
        await Assert.That(psi.UseShellExecute).IsFalse();
        await Assert.That(psi.RedirectStandardInput || psi.RedirectStandardOutput || psi.RedirectStandardError).IsFalse();
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/tmp/wd");
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Directory);
        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("work");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
        await Assert.That(result.Status).IsEqualTo(HandoffLaunchStatus.Ran);
    }

    [Test]
    public async Task Nonzero_exit_inside_two_seconds_is_a_launch_failure_but_a_zero_exit_is_not() {
        if (OperatingSystem.IsWindows()) return;

        var fail = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("false") { UseShellExecute = false }));
        var ok   = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("true")  { UseShellExecute = false }));

        await Assert.That(new HandoffAgentLauncher(Config.Root, fail).Launch(Request("/bin/false")).Status).IsEqualTo(HandoffLaunchStatus.LaunchFailed);
        await Assert.That(new HandoffAgentLauncher(Config.Root, ok).Launch(Request("/bin/true")).Status).IsEqualTo(HandoffLaunchStatus.Ran);
    }

    [Test]
    public async Task Nonzero_exit_after_two_seconds_counts_as_ran() {
        if (OperatingSystem.IsWindows()) return;

        var late = FakeProcessStarter.Running(_ => {
            var psi = new ProcessStartInfo("sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sleep 2.5; exit 3");
            return Process.Start(psi);
        });

        var result = new HandoffAgentLauncher(Config.Root, late).Launch(Request("/bin/sh"));

        await Assert.That(result.Status).IsEqualTo(HandoffLaunchStatus.Ran);
        await Assert.That(result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task A_refused_or_throwing_start_is_a_launch_failure() {
        await Assert.That(new HandoffAgentLauncher(Config.Root, FakeProcessStarter.Refusing()).Launch(Request("/x")).Status).IsEqualTo(HandoffLaunchStatus.LaunchFailed);
        await Assert.That(new HandoffAgentLauncher(Config.Root, FakeProcessStarter.Throwing(new InvalidOperationException("boom"))).Launch(Request("/x")).Error).Contains("boom");
    }
}
