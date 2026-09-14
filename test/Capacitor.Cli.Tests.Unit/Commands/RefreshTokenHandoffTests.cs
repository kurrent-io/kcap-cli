using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RefreshTokenHandoffTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The detached process refreshes the hook's own token: same config root, the profile
    /// pinned by name, and no URL override — which would outrank the pin and resolve to no profile.</summary>
    [Test]
    public async Task The_detached_process_is_pinned_to_the_hooks_root_and_profile() {
        var psi = RefreshTokenHandoff.BuildStartInfo(Config.Root, "work");

        await Assert.That(psi.ArgumentList).IsEquivalentTo([RefreshTokenHandoff.Command, RefreshTokenHandoff.DetachedFlag]);
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Root.Directory);
        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("work");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
    }

    /// <summary>Every standard stream is a pipe the spawner closes, so the child never holds the
    /// host's hook pipes open and a host waiting for EOF is not made to wait on the child.</summary>
    [Test]
    public async Task The_detached_process_inherits_none_of_the_hooks_streams() {
        var psi = RefreshTokenHandoff.BuildStartInfo(Config.Root, "work");

        await Assert.That(psi.RedirectStandardInput).IsTrue();
        await Assert.That(psi.RedirectStandardOutput).IsTrue();
        await Assert.That(psi.RedirectStandardError).IsTrue();
        await Assert.That(psi.UseShellExecute).IsFalse();
    }

    [Test]
    public async Task Only_the_flagged_refresh_command_counts_as_the_detached_continuation() {
        await Assert.That(RefreshTokenHandoff.IsDetached("refresh-token", ["refresh-token", "--detached"])).IsTrue();
        await Assert.That(RefreshTokenHandoff.IsDetached("refresh-token", ["refresh-token"])).IsFalse();
        await Assert.That(RefreshTokenHandoff.IsDetached("hook", ["hook", "--claude", "--detached"])).IsFalse();
    }
}
