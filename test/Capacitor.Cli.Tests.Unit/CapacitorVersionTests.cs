using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class CapacitorVersionTests {
    [Test]
    public async Task Current_returns_assembly_informational_version() {
        var version = CapacitorVersion.Current();
        await Assert.That(version).IsNotNull();
        await Assert.That(version).IsNotEmpty();
    }

    [Test]
    public async Task Current_matches_AgentsSkillsInstaller_CurrentVersion() {
        // Regression: every installer's marker must contain the same version
        // string so a postinstall checking one marker doesn't see drift caused
        // by two installers stamping different version values for the same build.
        await Assert.That(CapacitorVersion.Current())
            .IsEqualTo(AgentsSkillsInstaller.CurrentVersion());
    }
}
