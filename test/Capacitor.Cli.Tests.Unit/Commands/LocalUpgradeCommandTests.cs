using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The exit codes and the JSON result the desktop app and <c>kcap update</c> read.</summary>
[NotInParallel]
[ExcludeOn(OS.Windows)]
public class LocalUpgradeCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class StackFactory(Func<LocalStack> create) : ILocalStackFactory {
        public LocalStack Create() => create();

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    async Task<(int Exit, string Out)> RunAsync(string version, params string[] args) {
        Tmp.CreateDir("home");
        var home  = new UserHome(Tmp.GetResolvedPath("home"));
        var root  = Core.LocalEdition.LocalRoot.Resolve(Tmp.PathTo("r"), home);
        var stack = new LocalStack(root, home, new FakeLocalServerService([], home), new HttpClient(), TimeProvider.System,
            new FakeOutgoingRecorders([]), new ConfigRoot(Tmp.PathTo("cfg")));

        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await new LocalUpgradeCommand(new StackFactory(() => stack)) { Version = version }.HandleAsync(["local", "upgrade", .. args]);

        return (exit, capture.GetCapturedOutput());
    }

    [Test]
    public async Task Without_an_installation_json_reports_nothing_to_do_and_exits_3() {
        var (exit, output) = await RunAsync("1.3.0", "--json");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.NothingToDo);
        await Assert.That(output.Trim()).IsEqualTo("""{"v":1,"event":"nothing_to_do","version":"1.3.0","reason":"not_installed"}""");
    }

    [Test]
    public async Task If_installed_is_silent_without_an_installation() {
        var (exit, output) = await RunAsync("1.3.0", "--if-installed");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.NothingToDo);
        await Assert.That(output).IsEmpty();
    }

    [Test]
    public async Task A_build_that_is_not_a_release_names_no_bundle() {
        var (exit, output) = await RunAsync("unknown", "--json");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.Failed);
        await Assert.That(output).Contains("\"code\":\"invalid_arguments\"");
    }

    [Test]
    public async Task An_unknown_option_is_refused() {
        var (exit, output) = await RunAsync("1.3.0", "--json", "--bogus");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.Failed);
        await Assert.That(output).Contains("--bogus");
    }
}
