using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandSkillsTests {
    [Test]
    [NotInParallel("ConsoleStreams")]
    public async Task Install_with_both_codex_and_skills_flags_returns_error() {
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        try {
            Console.SetError(capturedErr);
            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex", "--skills"]);
            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capturedErr.ToString()).Contains("mutually exclusive");
        } finally {
            Console.SetError(originalErr);
        }
    }

    [Test]
    [NotInParallel("ConsoleStreams")]
    public async Task Remove_with_both_codex_and_skills_flags_returns_error() {
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        try {
            Console.SetError(capturedErr);
            var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--codex", "--skills"]);
            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capturedErr.ToString()).Contains("mutually exclusive");
        } finally {
            Console.SetError(originalErr);
        }
    }


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
    public async Task Install_skills_with_if_installed_is_noop_when_marker_absent() {
        var fakeHome      = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome  = Environment.GetEnvironmentVariable("HOME");
        var pluginPath    = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        var originalPlug  = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");

        try {
            // Seed a valid plugin source — proves the gate short-circuits
            // BEFORE attempting any work, not because the source is invalid.
            var skillsSrc = Path.Combine(pluginPath.FullName, "skills");
            Directory.CreateDirectory(skillsSrc);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(skillsSrc, name));
                await File.WriteAllTextAsync(
                    Path.Combine(skillsSrc, name, "SKILL.md"),
                    $"---\nname: {name}\n---\nbody");
            }

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // No marker existed → installer must not have run.
            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            await Assert.That(Directory.Exists(target)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_skills_with_if_installed_refreshes_when_marker_present() {
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
                    $"---\nname: {name}\ndescription: fresh\n---\nfresh body");
            }

            // Pre-seed marker (simulating a prior install).
            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(
                Path.Combine(target, AgentsSkillsInstaller.MarkerFileName),
                "old-version");

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // Skills must be present after refresh.
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(target, $"kapacitor-{name}"))).IsTrue();
            }

            // Marker must have been overwritten with the current assembly version.
            var currentMarker = await File.ReadAllTextAsync(Path.Combine(target, AgentsSkillsInstaller.MarkerFileName));
            await Assert.That(currentMarker.Trim()).IsNotEqualTo("old-version");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_skills_with_if_installed_refreshes_pre_marker_install() {
        // Regression: an existing install from a pre-marker build has owned
        // kapacitor-* folders but no marker file. The first upgrade-time
        // postinstall must still refresh it (and stamp the marker so future
        // upgrades take the marker-fast-path).
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var pluginPath   = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        var originalPlug = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");

        try {
            var skillsSrc = Path.Combine(pluginPath.FullName, "skills");
            Directory.CreateDirectory(skillsSrc);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(skillsSrc, name));
                await File.WriteAllTextAsync(
                    Path.Combine(skillsSrc, name, "SKILL.md"),
                    $"---\nname: {name}\ndescription: fresh\n---\nfresh body");
            }

            // Pre-marker install: kapacitor-* folder exists, no marker file.
            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            var staleSkill = Path.Combine(target, "kapacitor-recap");
            Directory.CreateDirectory(staleSkill);
            await File.WriteAllTextAsync(
                Path.Combine(staleSkill, "SKILL.md"),
                "---\nname: kapacitor-recap\n---\nstale body");
            await Assert.That(File.Exists(Path.Combine(target, AgentsSkillsInstaller.MarkerFileName))).IsFalse();

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // All skills present + freshly written.
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(target, $"kapacitor-{name}"))).IsTrue();
            }
            var refreshed = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
            await Assert.That(refreshed).Contains("fresh body");
            await Assert.That(refreshed).DoesNotContain("stale body");

            // Marker now stamped so the next upgrade takes the fast path.
            await Assert.That(File.Exists(Path.Combine(target, AgentsSkillsInstaller.MarkerFileName))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_skills_with_if_installed_is_noop_when_marker_matches_current_version() {
        // Fast path: same-version reinstalls (e.g. `npm install -g @kurrent/kapacitor`
        // when the same version is already installed) must not re-copy every skill.
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
                    $"---\nname: {name}\n---\nfresh body");
            }

            // Pre-seed: marker holds the *current* CLI version.
            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(
                Path.Combine(target, AgentsSkillsInstaller.MarkerFileName),
                AgentsSkillsInstaller.CurrentVersion());

            // Pre-seed one skill folder with a sentinel that the installer
            // would otherwise overwrite. If the short-circuit fires, this
            // file should survive untouched.
            Directory.CreateDirectory(Path.Combine(target, "kapacitor-recap"));
            await File.WriteAllTextAsync(
                Path.Combine(target, "kapacitor-recap", "SKILL.md"),
                "stale body — must NOT be overwritten");

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // Sentinel still intact → installer did not run.
            var preserved = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
            await Assert.That(preserved).IsEqualTo("stale body — must NOT be overwritten");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_skills_with_if_installed_swallows_plugin_resolution_failure() {
        var fakeHome      = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome  = Environment.GetEnvironmentVariable("HOME");
        var originalPlug  = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
        var originalErr   = Console.Error;
        var capturedErr   = new StringWriter();

        try {
            // Pre-seed marker so the gate proceeds…
            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(
                Path.Combine(target, AgentsSkillsInstaller.MarkerFileName),
                "some-version");

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            // …but make plugin resolution fail by pointing at a non-existent dir.
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR",
                Path.Combine(Path.GetTempPath(), $"kapacitor-missing-{Guid.NewGuid():N}"));
            Console.SetError(capturedErr);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills", "--if-installed"]);

            // Refresh path must never fail npm install — exit 0, nothing on stderr.
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capturedErr.ToString()).IsEmpty();
        } finally {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
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
