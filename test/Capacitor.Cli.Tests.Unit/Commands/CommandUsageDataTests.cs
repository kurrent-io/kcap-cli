using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

internal sealed class CommandUsageDataTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    LocalRoot Root => field ??= CreateRoot();

    LocalRoot CreateRoot() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        root.EnsureCreated();
        return root;
    }

    UnsavedConsent For(string commandLine, bool localAvailable = true) {
        var args = commandLine.Split(' ');

        return CommandUsageData.For(args[0], args, Config.Root, Root, localAvailable);
    }

    [Test]
    [Arguments("setup")]
    [Arguments("setup --local")]
    [Arguments("setup --local --single-user --yes --no-prompt")]
    [Arguments("setup --local --json")]
    [Arguments("setup --skip-import")]
    public async Task A_setup_that_may_end_up_local_holds_usage_data(string commandLine) {
        await Assert.That(For(commandLine)).IsEqualTo(UnsavedConsent.Pending);
    }

    [Test]
    [Arguments("setup acme")]
    [Arguments("setup --server-url https://acme.kcap.ai")]
    [Arguments("setup --server-url=https://acme.kcap.ai")]
    [Arguments("setup --org Acme --slug acme --no-prompt")]
    [Arguments("setup --discover")]
    [Arguments("login")]
    [Arguments("recap")]
    public async Task A_command_that_names_the_cloud_or_chooses_no_edition_keeps_the_notice(string commandLine) {
        await Assert.That(For(commandLine)).IsEqualTo(UnsavedConsent.Share);
    }

    [Test]
    public async Task Where_the_local_edition_cannot_run_setup_keeps_the_notice() {
        await Assert.That(For("setup", localAvailable: false)).IsEqualTo(UnsavedConsent.Share);
    }

    /// <summary>The local edition refuses a machine with a cloud profile, so a bare setup there is the cloud edition's.</summary>
    [Test]
    public async Task A_bare_setup_on_a_cloud_machine_keeps_the_notice() {
        ConfigMutator.Mutate(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "acme",
            Profiles      = new() { ["acme"] = new Profile { ServerUrl = "https://acme.kcap.ai" } },
        });

        await Assert.That(For("setup")).IsEqualTo(UnsavedConsent.Share);
    }

    [Test]
    [Arguments("recap")]
    [Arguments("mcp")]
    [Arguments("daemon start")]
    [Arguments("setup")]
    [Arguments("config show")]
    public async Task Every_command_on_a_local_machine_withholds_usage_data(string commandLine) {
        LocalStateFiles.PublishRegistry(Root, new InstallRegistry(new LocalInstallation("inst-1", 51234, "http://localhost:51234", "1.4.0"), []));

        await Assert.That(For(commandLine, localAvailable: false)).IsEqualTo(UnsavedConsent.Withhold);
    }
}
