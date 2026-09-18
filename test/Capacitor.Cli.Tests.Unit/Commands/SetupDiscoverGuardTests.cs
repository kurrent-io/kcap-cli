using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Pins what `--discover` accepts beside itself. A named workspace is refused rather than ignored,
/// wherever it sits, and so is any flag a run that configures nothing would have to drop.
/// </summary>
public class SetupDiscoverGuardTests {
    [Test]
    public async Task Discovery_and_the_flags_that_shape_it_are_accepted() {
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover"])).IsNull();
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--json"])).IsNull();
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--device", "--github"])).IsNull();
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--json", "--no-prompt"])).IsNull();
    }

    // Read by the update notice for every command, so a caller that always passes it must not be refused.
    [Test]
    public async Task The_process_wide_update_opt_out_is_accepted() =>
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--no-update-check"])).IsNull();

    [Test]
    [Arguments("setup acme --discover")]
    [Arguments("setup --discover acme")]
    [Arguments("setup --discover --json acme")]
    public async Task A_positional_workspace_is_refused_wherever_it_sits(string commandLine) =>
        await Assert.That(SetupCommand.DiscoverArgumentError(commandLine.Split(' ')))
            .Contains("cannot also be given one");

    [Test]
    public async Task Naming_a_server_or_a_workspace_to_create_is_refused_as_a_workspace() {
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--server-url", "https://acme.kcap.ai"]))
            .Contains("cannot also be given one");
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--org", "Acme", "--slug", "acme"]))
            .Contains("cannot also be given one");
    }

    // The flag is what gets named, not its value: "user" read as a workspace would tell the reader to
    // drop an argument they never gave.
    [Test]
    [Arguments("--plugin-scope", "user")]
    [Arguments("--default-visibility", "private")]
    [Arguments("--daemon-name", "laptop")]
    [Arguments("--use-provider-api-key", "true")]
    public async Task A_flag_that_configures_something_is_named_in_the_refusal(string flag, string value) {
        var refusal = SetupCommand.DiscoverArgumentError(["setup", "--discover", flag, value]);

        await Assert.That(refusal).Contains(flag);
        await Assert.That(refusal).DoesNotContain("cannot also be given one");
    }

    // A value that repeats the workspace's name must not hide the workspace.
    [Test]
    public async Task A_workspace_is_still_caught_when_a_later_value_repeats_it() =>
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "acme", "--daemon-name", "acme"]))
            .Contains("cannot also be given one");

    // …nor in the other order, where the value comes first and the workspace repeats it.
    [Test]
    public async Task A_workspace_repeating_an_earlier_value_does_not_start_discovery() =>
        await Assert.That(SetupCommand.DiscoverArgumentError(["setup", "--discover", "--profile", "acme", "acme"]))
            .IsNotNull();
}
