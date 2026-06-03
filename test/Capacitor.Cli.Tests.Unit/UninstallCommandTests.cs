using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

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

    [Test]
    public async Task Marker_only_install_is_purged_even_when_json_has_no_kapacitor_entries() {
        // Regression for the "marker survives manual JSON edit" leak: the
        // upstream plugin removers delete the marker only when JSON entries
        // changed. If a user removed kapacitor entries by hand earlier, the
        // marker survives and IsInstalled keeps returning true. uninstall
        // must always nuke the markers.
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        // settings.json present but no kapacitor entries.
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), """{"userKey":"x"}""");
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            KapacitorVersion.Current());

        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        await File.WriteAllTextAsync(Path.Combine(codexDir, "hooks.json"), """{"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName),
            KapacitorVersion.Current());

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        await File.WriteAllTextAsync(Path.Combine(cursorDir, "hooks.json"), """{"version":1,"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName),
            KapacitorVersion.Current());

        var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(codexDir,  CodexHooksInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName))).IsFalse();
    }

    [Test]
    public async Task Sweep_removes_kapacitor_prefixed_skill_folders_not_in_current_source_list() {
        // Regression for the "retired/renamed skill folder survives" leak:
        // AgentsSkillsInstaller.Remove uses the current SourceNames list, so
        // a kapacitor-* folder from an older release isn't matched. uninstall
        // must sweep the directory for our prefix to catch those.
        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);

        // A skill from the current list (handled by Remove) and a retired one
        // (only handled by the sweep). Plus a user-authored folder that must
        // survive both.
        var currentSkill = Path.Combine(skillsDir, $"kapacitor-{AgentsSkillsInstaller.SourceNames[0]}");
        var retiredSkill = Path.Combine(skillsDir, "kapacitor-retired-from-v0.42");
        var userSkill    = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(currentSkill);
        Directory.CreateDirectory(retiredSkill);
        Directory.CreateDirectory(userSkill);

        // Legacy Codex skills dir: same story.
        var legacyDir = Path.Combine(fixture.Home, ".codex", "skills");
        Directory.CreateDirectory(legacyDir);
        var legacyRetired = Path.Combine(legacyDir, "kapacitor-also-retired");
        Directory.CreateDirectory(legacyRetired);

        var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(Directory.Exists(currentSkill)).IsFalse();
        await Assert.That(Directory.Exists(retiredSkill)).IsFalse();
        await Assert.That(Directory.Exists(legacyRetired)).IsFalse();
        await Assert.That(Directory.Exists(userSkill)).IsTrue();
    }

    [Test]
    public async Task Config_dir_is_preserved_when_user_level_steps_fail() {
        // Regression for the "discarded return codes" issue: when a step
        // returns non-zero, uninstall must NOT delete ~/.config/kapacitor —
        // doing so destroys the only state that lets the user re-run and
        // finish, and it would silently report success.
        //
        // Windows file ACLs would need an entirely different setup, so this
        // case is Unix-only. The aggregation logic is the same on every
        // platform; covering Unix is sufficient for regression purposes.
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await Fixture.CreateAsync();

        // Force PluginCommand's Claude remove to return 1 by leaving a valid
        // kapacitor entry behind a read-only file: File.Exists passes, JSON
        // parses cleanly, then File.WriteAllText hits UnauthorizedAccessException
        // — which the remover's catch translates into exit code 1.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"enabledPlugins": {"kapacitor@kapacitor": true}}
            """);
        File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"survives-partial-failure"}""");

        try {
            var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("survives-partial-failure");
        } finally {
            // Restore write so the test fixture can be deleted.
            File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Cursor_hooks_write_failure_propagates_to_exit_code() {
        // Cursor's RemoveCursor previously returned 0 even when the helper's
        // inner try/catch swallowed a write failure. The helper now throws,
        // RemoveCursor catches + returns 1, and uninstall aggregates that
        // into hadFailures.
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var hooksPath = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kapacitor hook --cursor"}]}}
            """);
        File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try {
            var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            // Failure path skips the config-dir delete so the user can re-run.
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Sweep_failure_propagates_to_exit_code() {
        // SweepKapacitorPrefixedDirs previously logged Directory.Delete errors
        // but never flipped hadFailures, so a stuck kapacitor-* folder under
        // ~/.agents/skills/ would leave uninstall claiming success.
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(Path.Combine(skillsDir, "kapacitor-stuck"));

        // Strip write permission on the parent. Directory.Delete on a child
        // needs write+exec on the parent on every Unix; without write the
        // unlink fails with EACCES and Directory.Delete surfaces it.
        File.SetUnixFileMode(skillsDir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try {
            var exit = await UninstallCommand.HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            // Restore so the fixture can be cleaned up.
            File.SetUnixFileMode(skillsDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
