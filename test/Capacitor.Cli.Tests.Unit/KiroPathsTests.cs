using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Tests.Unit;

public class KiroPathsTests {
    // Parallel-safe: asserts an invariant relationship (mcp.json is a sibling of cli.json under
    // settings/), so it holds regardless of how ConfigRoot resolves (KIRO_HOME / home).
    [Test]
    public async Task SettingsMcpJson_is_mcp_json_sibling_of_cli_json() {
        var mcp = KiroPaths.SettingsMcpJson(home: "/fake/home");
        var cli = KiroPaths.SettingsFile(home: "/fake/home");

        await Assert.That(Path.GetFileName(mcp)).IsEqualTo("mcp.json");
        await Assert.That(Path.GetDirectoryName(mcp)).IsEqualTo(Path.GetDirectoryName(cli));
    }

    // Parallel-safe: home is non-null, so no env var is read. Skills live at <config-root>/skills
    // (a sibling of settings/agents), NOT the agent-agnostic ~/.agents/skills, which Kiro can't read.
    [Test]
    public async Task SkillsDir_is_kiro_skills_not_agents_skills() {
        var skills = KiroPaths.SkillsDir(home: "/fake/home");

        await Assert.That(skills).IsEqualTo(Path.Combine("/fake/home", ".kiro", "skills"));
        await Assert.That(skills).DoesNotContain(Path.Combine(".agents", "skills"));
    }
}
