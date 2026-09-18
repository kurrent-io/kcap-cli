using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsLocksTests {
    [Test]
    public async Task The_migration_lock_is_one_key_for_the_whole_machine() {
        // Legacy ownership crosses repositories, so two repositories must not hold two keys.
        await Assert.That(SkillsLocks.Migration).IsEqualTo("skills/legacy-migration");
        await Assert.That(SkillsLocks.Migration).IsNotEqualTo(SkillsLocks.Repository("aaaa"));
        await Assert.That(SkillsLocks.Repository("aaaa")).IsNotEqualTo(SkillsLocks.Repository("bbbb"));
        await Assert.That(SkillsLocks.Manifest("/a/.git", "claude"))
            .IsNotEqualTo(SkillsLocks.Manifest("/a/.git", "agents"));
        await Assert.That(SkillsLocks.Manifest("/a/.git", "claude"))
            .IsNotEqualTo(SkillsLocks.Manifest("/b/.git", "claude"));
    }
}
