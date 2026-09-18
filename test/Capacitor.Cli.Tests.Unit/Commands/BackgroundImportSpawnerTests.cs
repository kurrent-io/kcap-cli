using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Bare <c>[NotInParallel]</c>: <see cref="PollutedParent"/> seeds <c>KCAP_URL</c> in the
/// process environment, which every spawned child inherits.</summary>
[NotInParallel]
public class BackgroundImportSpawnerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // RunId names the log file: a distinct one is required whenever a test spawns twice against
    // the same Config.Root, or the second CreateNew collides with the first spawn's own file.
    static BackgroundImportRequest Request(string profile = "work", string runId = "0123456789abcdef0123456789abcdef") =>
        new(runId, profile, "private", "/tmp/wd");

    /// A parent environment carrying KCAP_URL is what makes the removal observable.
    static IDisposable PollutedParent() => EnvScope.Exclusive(ProfileOverrides.UrlVar, "https://ambient.test");

    [Test]
    public async Task Argv_and_environment_pin_the_saved_profile_and_drop_the_url_override() {
        if (OperatingSystem.IsWindows()) return;
        using var _ = PollutedParent();
        var starter = FakeProcessStarter.Running(psi => Process.Start(new ProcessStartInfo("sleep", "5") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));

        var launch = new BackgroundImportSpawner(Config.Root, starter).Spawn(Request());

        var psi = starter.Seen!;
        await Assert.That(psi.ArgumentList.ToList()).IsEquivalentTo(["import", "--all", "--yes", "--skip-title"]);
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Directory);
        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("work");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
        await Assert.That(psi.Environment[DetachedImportLog.EnvVar]).IsEqualTo(Config.Root.Path("import-0123456789abcdef0123456789abcdef.log"));
        await Assert.That(psi.Environment[DetachedImportLog.VisibilityEnvVar]).IsEqualTo("private");
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/tmp/wd");
        await Assert.That(psi.UseShellExecute).IsFalse();
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
        await Assert.That(launch.Status).IsEqualTo(BackgroundImportStatus.Running);
        await Assert.That(File.Exists(launch.LogPath!)).IsTrue();
    }

    [Test]
    public async Task Early_zero_exit_is_ExitedZero_and_early_nonzero_is_Failed() {
        if (OperatingSystem.IsWindows()) return;
        var zero = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("true") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));
        var fail = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("false") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));

        var a = new BackgroundImportSpawner(Config.Root, zero).Spawn(Request());
        var b = new BackgroundImportSpawner(Config.Root, fail).Spawn(Request("other", "fedcba9876543210fedcba9876543210"));

        await Assert.That(a.Status).IsEqualTo(BackgroundImportStatus.ExitedZero);
        await Assert.That(b.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(b.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_start_or_a_throw_is_Failed() {
        var refused = new BackgroundImportSpawner(Config.Root, FakeProcessStarter.Refusing()).Spawn(Request());
        var thrown  = new BackgroundImportSpawner(Config.Root, FakeProcessStarter.Throwing(new InvalidOperationException("no exec")))
            .Spawn(Request(runId: "fedcba9876543210fedcba9876543210"));

        await Assert.That(refused.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(thrown.Error).Contains("no exec");
    }

    [Test]
    public async Task A_pre_existing_path_at_the_log_name_fails_the_spawn_and_is_left_untouched() {
        var path = Config.Root.Path("import-0123456789abcdef0123456789abcdef.log");
        await File.WriteAllTextAsync(path, "someone else's");
        var starter = FakeProcessStarter.Refusing();

        var launch = new BackgroundImportSpawner(Config.Root, starter).Spawn(Request());

        await Assert.That(launch.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(starter.Starts).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("someone else's");
    }

    [Test]
    public async Task An_unwritable_config_dir_fails_the_spawn_instead_of_throwing() {
        if (OperatingSystem.IsWindows()) return; // no mode bits to take away
        if (Environment.UserName == "root") return; // root ignores the missing write bit
        var starter = FakeProcessStarter.Refusing();
        File.SetUnixFileMode(Config.Directory, UnixFileMode.UserRead);
        try {
            var launch = new BackgroundImportSpawner(Config.Root, starter).Spawn(Request());

            await Assert.That(launch.Status).IsEqualTo(BackgroundImportStatus.Failed);
            await Assert.That(starter.Starts).IsEqualTo(0);
            await Assert.That(launch.Error).Contains("could not create");
        } finally {
            File.SetUnixFileMode(Config.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
