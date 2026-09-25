using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class KiroCrewPathsTests {
    [Test]
    public async Task Crew_root_defaults_under_the_kiro_root_and_honours_its_override() {
        await Assert.That(new KiroCrewPaths("/h/.kiro", null).Root).IsEqualTo(Path.Combine("/h/.kiro", "crew"));
        await Assert.That(new KiroCrewPaths("/h/.kiro", "/custom/crew").Root).IsEqualTo("/custom/crew");
    }

    /// <summary>Crew reads hook scripts from <c>~/.kiro/hooks</c> even when its own root is moved.</summary>
    [Test]
    public async Task Hook_script_stays_under_the_kiro_root_when_the_crew_root_moves() {
        var paths = new KiroCrewPaths("/h/.kiro", "/custom/crew");

        await Assert.That(paths.SpawnHookScript).IsEqualTo(Path.Combine("/h/.kiro", "hooks", "kcap-spawn.sh"));
        await Assert.That(paths.SkillsDir).IsEqualTo(Path.Combine("/custom/crew", "skills"));
    }

    [Test]
    public async Task A_harness_over_an_isolated_kiro_root_keeps_crew_inside_it() {
        var harness = KiroHarness.Over(new KiroPaths(new("/iso"), null));

        await Assert.That(harness.Crew.Root).IsEqualTo(Path.Combine(harness.Paths.ConfigRoot, "crew"));
    }
}
