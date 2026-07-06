using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Tests.Unit;

public class CuratedTargetsTests {
    const string Root = "/repo";
    static string Claude => Path.Combine(Root, "CLAUDE.md");
    static string Agents => Path.Combine(Root, "AGENTS.md");

    [Test]
    public async Task With_content_both_exist_targets_both() {
        var r = CuratedTargets.Resolve(Root, claudeMdExists: true, agentsMdExists: true, hasContent: true);
        await Assert.That(r).IsEquivalentTo([Claude, Agents]);
    }

    [Test]
    public async Task With_content_only_one_targets_that_one() {
        await Assert.That(CuratedTargets.Resolve(Root, true, false, true)).IsEquivalentTo([Claude]);
        await Assert.That(CuratedTargets.Resolve(Root, false, true, true)).IsEquivalentTo([Agents]);
    }

    [Test]
    public async Task With_content_neither_creates_agents() {
        await Assert.That(CuratedTargets.Resolve(Root, false, false, true)).IsEquivalentTo([Agents]);
    }

    [Test]
    public async Task Empty_set_never_creates_targets_only_existing() {
        await Assert.That(CuratedTargets.Resolve(Root, false, false, false)).IsEmpty();
        await Assert.That(CuratedTargets.Resolve(Root, true, false, false)).IsEquivalentTo([Claude]);
        await Assert.That(CuratedTargets.Resolve(Root, true, true, false)).IsEquivalentTo([Claude, Agents]);
    }
}
