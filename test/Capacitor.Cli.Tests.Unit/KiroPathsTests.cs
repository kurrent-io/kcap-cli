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

    // Parallel-safe: asserts an invariant relationship (skills is <config-root>/skills), so it holds
    // regardless of how ConfigRoot resolves (KIRO_HOME / home). This is the kcap-owned Kiro skills dir
    // — a sibling of settings/agents under the Kiro config root — NOT the agent-agnostic ~/.agents/skills.
    [Test]
    public async Task SkillsDir_is_skills_under_the_kiro_config_root() {
        var skills = KiroPaths.SkillsDir(home: "/fake/home");
        var root   = KiroPaths.ConfigRoot(home: "/fake/home");

        await Assert.That(Path.GetFileName(skills)).IsEqualTo("skills");
        await Assert.That(Path.GetDirectoryName(skills)).IsEqualTo(root);   // under the Kiro root, not ~/.agents
    }
}
