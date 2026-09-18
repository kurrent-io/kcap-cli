using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Pins the guard that keeps `--discover` from being handed the answer it exists to find. A named
/// workspace is refused rather than ignored, wherever it sits.
/// </summary>
public class SetupDiscoverGuardTests {
    [Test]
    public async Task Plain_discovery_names_nothing() {
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover"])).IsFalse();
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--json"])).IsFalse();
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--device", "--github"])).IsFalse();
    }

    // A valued flag's value is not a workspace: "private" here is the visibility, not a slug.
    [Test]
    public async Task A_valued_flags_value_is_not_a_workspace() {
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--default-visibility", "private"]))
            .IsFalse();
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--daemon-name", "laptop"])).IsFalse();
    }

    [Test]
    public async Task A_positional_slug_is_caught_wherever_it_sits() {
        // The tenant argument's usual position.
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "acme", "--discover"])).IsTrue();
        // …and after the flag, which is where it slips past a check that only reads args[1].
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "acme"])).IsTrue();
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--json", "acme"])).IsTrue();
    }

    [Test]
    public async Task Naming_a_server_or_a_workspace_to_create_counts_too() {
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--server-url", "https://acme.kcap.ai"]))
            .IsTrue();
        await Assert.That(SetupCommand.NamesAWorkspace(["setup", "--discover", "--org", "Acme", "--slug", "acme"]))
            .IsTrue();
    }
}
