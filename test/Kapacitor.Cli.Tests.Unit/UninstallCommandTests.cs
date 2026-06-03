using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

// HomeEnvVarMutation: uninstall reads HOME via PathHelpers.HomeDirectory to
// resolve every user-level path (~/.claude, ~/.codex, ~/.cursor, ~/.agents).
// ConfigDirEnvVar: uninstall reads KAPACITOR_CONFIG_DIR fresh on every call,
// so we mutate it per-test to point at the test's temp dir without disturbing
// the assembly-wide value pinned by RepoPathStoreGlobalSetup.
[NotInParallel(["HomeEnvVarMutation", "ConfigDirEnvVar", "CwdMutation"])]
public class UninstallCommandTests {
    [Test]
    public async Task User_level_uninstall_removes_kapacitor_entries_and_preserves_user_data() {
        await using var fixture = await Fixture.CreateAsync();

        // Seed Claude user settings with kapacitor + user-authored content.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {
              "userKey": "must-survive",
              "extraKnownMarketplaces": {
                "kapacitor":  { "source": { "source": "directory", "path": "/p" } },
                "userMarket": { "source": { "source": "directory", "path": "/u" } }
              },
              "enabledPlugins": { "kapacitor@kapacitor": true, "user@market": true }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            KapacitorVersion.Current());

        // Codex user hooks with a non-kapacitor entry alongside kapacitor.
        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexHooks = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(codexHooks, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        // Cursor user hooks similarly.
        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var cursorHooks = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(cursorHooks, """
            {
              "version": 1,
              "hooks": {
                "sessionStart": [
                  { "command": "user-script" },
                  { "command": "kapacitor hook --cursor" }
                ]
              }
            }
            """);

        // Skills present in ~/.agents/skills with marker.
        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            Directory.CreateDirectory(Path.Combine(skillsDir, $"kapacitor-{name}"));
        }
        var userSkill = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(userSkill);
        await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), "user content");

        // Seed config dir with a real file so we can verify deletion.
        await File.WriteAllTextAsync(Path.Combine(fixture.ConfigDir, "profiles.json"), "{}");

        var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes"]);
        await Assert.That(exit).IsEqualTo(0);

        // Claude: kapacitor entries gone, user entries preserved, marker removed.
        var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(claudeRoot["userKey"]!.GetValue<string>()).IsEqualTo("must-survive");
        await Assert.That(claudeRoot["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();
        await Assert.That(claudeRoot["enabledPlugins"]!["user@market"]!.GetValue<bool>()).IsTrue();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["kapacitor"]).IsNull();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["userMarket"]).IsNotNull();
        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();

        // Codex: kapacitor entry gone, user entry preserved.
        var codexRoot = JsonNode.Parse(await File.ReadAllTextAsync(codexHooks))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Cursor: kapacitor entry gone, user entry preserved.
        var cursorRoot   = JsonNode.Parse(await File.ReadAllTextAsync(cursorHooks))!.AsObject();
        var sessionStart2 = cursorRoot["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(sessionStart2.Count).IsEqualTo(1);
        await Assert.That(sessionStart2[0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Skills: kapacitor-* folders removed, user-authored folder intact.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(skillsDir, $"kapacitor-{name}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(userSkill)).IsTrue();

        // Config dir fully deleted.
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
    }

    [Test]
    public async Task Keep_config_preserves_config_dir_but_removes_integrations() {
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {"enabledPlugins": {"kapacitor@kapacitor": true}}
            """);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"keep"}""");

        var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(root["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();

        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("keep");
    }

    [Test]
    public async Task Project_flag_strips_project_scope_entries() {
        await using var fixture = await Fixture.CreateAsync();

        // Fake project: a temp dir with a .git directory makes GitRepository.FindRoot
        // return it as the working tree root.
        var projectDir = Directory.CreateTempSubdirectory("kapacitor-uninstall-project-");
        Directory.CreateDirectory(Path.Combine(projectDir.FullName, ".git"));

        var projectClaude = Path.Combine(projectDir.FullName, ".claude", "settings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectClaude)!);
        await File.WriteAllTextAsync(projectClaude, """
            {
              "userLocal": "keep",
              "enabledPlugins": { "kapacitor@kapacitor": true }
            }
            """);

        var projectCodex = Path.Combine(projectDir.FullName, ".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectCodex)!);
        await File.WriteAllTextAsync(projectCodex, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var originalCwd = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = projectDir.FullName;

            var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes", "--project", "--keep-config"]);
            await Assert.That(exit).IsEqualTo(0);

            var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(projectClaude))!.AsObject();
            await Assert.That(claudeRoot["userLocal"]!.GetValue<string>()).IsEqualTo("keep");
            await Assert.That(claudeRoot["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();

            var codexRoot    = JsonNode.Parse(await File.ReadAllTextAsync(projectCodex))!.AsObject();
            var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
            await Assert.That(sessionStart.Count).IsEqualTo(1);
            await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
        } finally {
            Environment.CurrentDirectory = originalCwd;
            projectDir.Delete(recursive: true);
        }
    }

    [Test]
    [NotInParallel(["ConsoleStreams", "HomeEnvVarMutation", "ConfigDirEnvVar", "CwdMutation"])]
    public async Task Project_flag_errors_when_not_inside_git_tree() {
        await using var fixture = await Fixture.CreateAsync();

        // A scratch dir with NO .git anywhere up the tree.
        var noRepoDir = Directory.CreateTempSubdirectory("kapacitor-uninstall-norepo-");
        var originalCwd = Environment.CurrentDirectory;
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();

        try {
            Environment.CurrentDirectory = noRepoDir.FullName;
            Console.SetError(capturedErr);

            var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes", "--project"]);
            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capturedErr.ToString()).Contains("--project requires a git working tree");
        } finally {
            Console.SetError(originalErr);
            Environment.CurrentDirectory = originalCwd;
            noRepoDir.Delete(recursive: true);
        }
    }

    sealed class Fixture : IAsyncDisposable {
        public required string Home      { get; init; }
        public required string ConfigDir { get; init; }

        public string? OriginalHome      { get; init; }
        public string? OriginalConfigDir { get; init; }

        public static Task<Fixture> CreateAsync() {
            var home      = Directory.CreateTempSubdirectory("kapacitor-uninstall-home-").FullName;
            var configDir = Path.Combine(home, ".config", "kapacitor");
            Directory.CreateDirectory(configDir);

            var f = new Fixture {
                Home              = home,
                ConfigDir         = configDir,
                OriginalHome      = Environment.GetEnvironmentVariable("HOME"),
                OriginalConfigDir = Environment.GetEnvironmentVariable("KAPACITOR_CONFIG_DIR"),
            };

            Environment.SetEnvironmentVariable("HOME", home);
            // Pin the config dir under the test home so uninstall's
            // Directory.Delete only touches the test's temp tree, never the
            // assembly-wide config dir pinned by RepoPathStoreGlobalSetup.
            Environment.SetEnvironmentVariable("KAPACITOR_CONFIG_DIR", configDir);

            return Task.FromResult(f);
        }

        public ValueTask DisposeAsync() {
            Environment.SetEnvironmentVariable("HOME", OriginalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_CONFIG_DIR", OriginalConfigDir);
            try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }
}
