using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandSkillsTests {
    [Test]
    public async Task Install_skills_writes_to_agents_dir_and_cleans_legacy() {
        var fakeHome      = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome  = Environment.GetEnvironmentVariable("HOME");
        var pluginPath    = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        var originalPlug  = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");

        try {
            var skillsSrc = Path.Combine(pluginPath.FullName, "skills");
            Directory.CreateDirectory(skillsSrc);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(skillsSrc, name));
                await File.WriteAllTextAsync(
                    Path.Combine(skillsSrc, name, "SKILL.md"),
                    $"---\nname: {name}\n---\nbody");
            }

            var legacyDir = Path.Combine(fakeHome.FullName, ".codex", "skills");
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(Path.Combine(legacyDir, "kapacitor-recap"));

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills"]);
            await Assert.That(exit).IsEqualTo(0);

            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(target, $"kapacitor-{name}"))).IsTrue();
            }
            await Assert.That(Directory.Exists(Path.Combine(legacyDir, "kapacitor-recap"))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Remove_skills_clears_agents_and_legacy() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            var agentsDir = Path.Combine(fakeHome.FullName, ".agents", "skills");
            var legacyDir = Path.Combine(fakeHome.FullName, ".codex", "skills");
            Directory.CreateDirectory(agentsDir);
            Directory.CreateDirectory(legacyDir);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(agentsDir, $"kapacitor-{name}"));
            }
            foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
                Directory.CreateDirectory(Path.Combine(legacyDir, name));
            }

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--skills"]);
            await Assert.That(exit).IsEqualTo(0);

            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(agentsDir, $"kapacitor-{name}"))).IsFalse();
            }
            await Assert.That(Directory.Exists(legacyDir)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }
}
