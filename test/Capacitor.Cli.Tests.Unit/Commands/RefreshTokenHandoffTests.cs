using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RefreshTokenHandoffTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The detached process runs the refresh command against the hook's own config root: a
    /// child resolving a different root would read, and rotate, a different token.</summary>
    [Test]
    public async Task The_detached_process_runs_refresh_token_against_the_hooks_config_root() {
        var psi = RefreshTokenHandoff.BuildStartInfo(Config.Root);

        await Assert.That(psi.ArgumentList).IsEquivalentTo([RefreshTokenHandoff.Command, RefreshTokenHandoff.DetachedFlag]);
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Root.Directory);
        await Assert.That(psi.RedirectStandardOutput).IsFalse();
        await Assert.That(psi.UseShellExecute).IsFalse();
    }
}
