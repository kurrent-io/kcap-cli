using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class LocalCommandTests {
    sealed class NoStack : ILocalStackFactory {
        public Core.LocalEdition.LocalRoot Root => throw new InvalidOperationException("The command read the root without a platform refusal.");

        public LocalStack Create() => throw new InvalidOperationException("The command reached the installation.");

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    sealed class NoBrowser : IBrowserLauncher {
        public bool TryOpen(string url) => throw new InvalidOperationException("The command opened a browser.");
    }

    /// <summary>A stop persists across logins, so a mistyped flag must not reach the installation.</summary>
    [Test]
    [Arguments("status")]
    [Arguments("start")]
    [Arguments("stop")]
    [Arguments("restart")]
    [Arguments("open")]
    public async Task A_trailing_argument_is_refused_before_anything_runs(string sub) {
        using var stderr = ConsoleOutput.StartErrorCapture();

        var exit = await new LocalCommand(new NoStack(), new LocalUpgradeCommand(new NoStack()), new LocalFetchCommand(new NoStack()), new NoBrowser(), new FakeOutgoingRecorders([]), new ConfigRoot("/no-config"), new UserHome("/no-home")).HandleAsync(["local", sub, "--bogus"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.GetCapturedError()).Contains("takes no arguments: --bogus");
    }
}
