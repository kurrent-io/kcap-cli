using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class VendorHomeVariableTests {
    [TempHome] public required TempHome Home { get; init; }

    /// <summary>The trailing separator spelling still names the built-in directory.</summary>
    [Test]
    [Arguments(HarnessId.Claude, ".claude", "CLAUDE_CONFIG_DIR")]
    [Arguments(HarnessId.Codex, ".codex", "CODEX_HOME")]
    public async Task Built_in_directory_plans_a_removal(HarnessId vendor, string builtIn, string variable) {
        await Assert.That(VendorHomeVariable.Plan(vendor, Home.PathTo(builtIn), Home)).IsEqualTo((variable, (string?)null));
        await Assert.That(VendorHomeVariable.Plan(vendor, Home.PathTo(builtIn) + Path.DirectorySeparatorChar, Home))
            .IsEqualTo((variable, (string?)null));
    }

    [Test]
    [Arguments(HarnessId.Claude, ".claude-work", "CLAUDE_CONFIG_DIR")]
    [Arguments(HarnessId.Codex, ".codex-work", "CODEX_HOME")]
    public async Task Custom_directory_plans_a_set(HarnessId vendor, string custom, string variable) {
        var directory = Home.PathTo(custom);

        await Assert.That(VendorHomeVariable.Plan(vendor, directory, Home)).IsEqualTo((variable, (string?)directory));
    }

    /// <summary>The built-in directory is defined for Claude and Codex only, so this also pins that the
    /// plan never asks for one it does not need.</summary>
    [Test]
    public async Task Vendor_without_a_home_variable_plans_nothing() {
        await Assert.That(VendorHomeVariable.NameOf(HarnessId.Cursor)).IsNull();
        await Assert.That(VendorHomeVariable.Plan(HarnessId.Cursor, Home.PathTo(".cursor"), Home)).IsNull();
    }
}
