using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginEnvironmentTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>
    /// The agents tree follows a rebound home. A record's copy constructor re-runs no initialiser, so
    /// a value captured at construction would leave a <c>with</c>-rebound environment writing skills
    /// under the home it was built from — the developer's real one, in a test.
    /// </summary>
    [Test]
    public async Task agents_tree_follows_a_rebound_home() {
        var other = new UserHome(Tmp.PathTo("other-home"));

        var rebound = TestEnv(Tmp.PathTo("original-home")) with { Home = other };

        await Assert.That(rebound.Agents.UserSkillsDir).IsEqualTo(new AgentsPaths(other).UserSkillsDir);
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        Home:              new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    ) {
        Harnesses = TestHarnesses.Under(new(fakeHome)),
    };
}
