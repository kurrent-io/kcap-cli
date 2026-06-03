using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class AgentsPathsTests {
    [Test]
    public async Task Home_resolves_under_HOME_dot_agents() {
        var tmp = Directory.CreateTempSubdirectory("kcap-agentspaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.Home; // force any static init
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(AgentsPaths.Home).IsEqualTo(Path.Combine(tmp.FullName, ".agents"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UserSkillsDir_resolves_under_HOME_dot_agents_skills() {
        var tmp = Directory.CreateTempSubdirectory("kcap-agentspaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.UserSkillsDir;
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(AgentsPaths.UserSkillsDir)
                .IsEqualTo(Path.Combine(tmp.FullName, ".agents", "skills"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }
}
