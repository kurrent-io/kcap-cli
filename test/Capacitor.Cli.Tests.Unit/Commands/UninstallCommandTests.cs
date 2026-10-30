using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class UninstallCommandTests {
    // Uninstall runs `daemon stop --yes`, which enumerates this directory and kills the PIDs it
    // finds; a shared one holding the test runner's own PID makes it kill its own tree.
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task User_level_uninstall_removes_kcap_entries_and_preserves_user_data() {
        await using var fixture = await Fixture.CreateAsync();

        // Seed Claude user settings with kcap + user-authored content.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {
              "userKey": "must-survive",
              "extraKnownMarketplaces": {
                "kcap":  { "source": { "source": "directory", "path": "/p" } },
                "userMarket": { "source": { "source": "directory", "path": "/u" } }
              },
              "enabledPlugins": { "kcap@kcap": true, "user@market": true }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        // Codex user hooks with a non-kcap entry alongside kcap.
        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexHooks = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(codexHooks, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
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
                  { "command": "kcap hook --cursor" }
                ]
              }
            }
            """);

        // Gemini shared settings.json: kcap hook + user hook + unrelated setting, plus marker.
        var geminiDir = Path.Combine(fixture.Home, ".gemini");
        Directory.CreateDirectory(geminiDir);
        var geminiSettings = Path.Combine(geminiDir, "settings.json");
        await File.WriteAllTextAsync(geminiSettings, """
            {
              "theme": "keep-me",
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script" }] },
                  { "hooks": [{ "type": "command", "command": "kcap hook --gemini" }] }
                ]
              }
            }
            """);
        GeminiHooksInstaller.WriteMarker(geminiSettings);

        // Skills present in ~/.agents/skills with marker.
        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            Directory.CreateDirectory(Path.Combine(skillsDir, $"kcap-{name}"));
        }
        var userSkill = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(userSkill);
        await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), "user content");

        // Seed config dir with a real file so we can verify deletion.
        await File.WriteAllTextAsync(Path.Combine(fixture.ConfigDir, "profiles.json"), "{}");

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fixture.Out.ToString()).Contains("Restart any coding agents that were open during uninstall");

        // Claude: kcap entries gone, user entries preserved, marker removed.
        var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(claudeRoot["userKey"]!.GetValue<string>()).IsEqualTo("must-survive");
        await Assert.That(claudeRoot["enabledPlugins"]!["kcap@kcap"]).IsNull();
        await Assert.That(claudeRoot["enabledPlugins"]!["user@market"]!.GetValue<bool>()).IsTrue();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["kcap"]).IsNull();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["userMarket"]).IsNotNull();
        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();

        // Codex: kcap entry gone, user entry preserved.
        var codexRoot = JsonNode.Parse(await File.ReadAllTextAsync(codexHooks))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Cursor: kcap entry gone, user entry preserved.
        var cursorRoot   = JsonNode.Parse(await File.ReadAllTextAsync(cursorHooks))!.AsObject();
        var sessionStart2 = cursorRoot["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(sessionStart2.Count).IsEqualTo(1);
        await Assert.That(sessionStart2[0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Gemini: kcap hook gone, user hook + unrelated setting preserved, marker removed.
        var geminiRoot  = JsonNode.Parse(await File.ReadAllTextAsync(geminiSettings))!.AsObject();
        await Assert.That(geminiRoot["theme"]!.GetValue<string>()).IsEqualTo("keep-me");
        var geminiStart = geminiRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(geminiStart.Count).IsEqualTo(1);
        await Assert.That(geminiStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
        await Assert.That(GeminiHooksInstaller.IsInstalled(geminiSettings)).IsFalse();

        // Skills: kcap-* folders removed, user-authored folder intact.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(skillsDir, $"kcap-{name}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(userSkill)).IsTrue();

        // Config dir fully deleted.
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_removes_pi_extension() {
        // Pi has no hook file — its live-ingest integration is the
        // ~/.pi/agent/extensions/kcap.ts extension. uninstall must delete it
        // (+ the version marker) or pi keeps auto-loading kcap.ts after the user
        // removed kcap. A user-authored sibling extension must survive.
        await using var fixture = await Fixture.CreateAsync();

        var extDir = Path.Combine(fixture.Home, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var kcapTs    = Path.Combine(extDir, "kcap.ts");
        var markerPi  = Path.Combine(extDir, ".kcap-extension-version");
        var userExt   = Path.Combine(extDir, "user-ext.ts");
        await File.WriteAllTextAsync(kcapTs, "export default function(pi){}");
        await File.WriteAllTextAsync(markerPi, CapacitorVersion.Current());
        await File.WriteAllTextAsync(userExt, "export default function(pi){}");

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(kcapTs)).IsFalse();
        await Assert.That(File.Exists(markerPi)).IsFalse();
        await Assert.That(File.Exists(userExt)).IsTrue();
    }

    [Test]
    public async Task User_level_uninstall_removes_muse_hooks_and_mcp_but_keeps_user_settings() {
        await using var fixture = await Fixture.CreateAsync();
        var museSettings = TestHarnesses.Under(fixture.UserHome).Of<MuseHarness>().Paths.SettingsJson;
        Directory.CreateDirectory(Path.GetDirectoryName(museSettings)!);
        await File.WriteAllTextAsync(museSettings, """{"schema_version":1,"theme":"keep-me"}""");
        MuseHooksInstaller.Install(museSettings);
        MuseSettingsJson.RegisterKcapMcpServers(museSettings, fixture.UserHome);

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(museSettings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(root)).IsEmpty();
        await Assert.That(MuseSettingsJson.OwnsAnything(museSettings, fixture.UserHome)).IsFalse();
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("keep-me");
    }

    [Test]
    public async Task User_level_uninstall_removes_kiro_agent() {
        // Kiro has no shell hooks — its integration is the kcap-owned agent file
        // ~/.kiro/agents/kcap.json (+ version marker), and install flips
        // chat.defaultAgent to it. uninstall must delete the agent file + marker
        // (otherwise `kiro` keeps loading kcap's hooks). A user-authored sibling
        // agent must survive. No ~/.kiro/settings/cli.json is seeded, so the
        // default-agent restore is correctly skipped (no kiro-cli needed).
        await using var fixture = await Fixture.CreateAsync();

        var agentsDir = Path.Combine(fixture.Home, ".kiro", "agents");
        Directory.CreateDirectory(agentsDir);
        var kcapAgent = Path.Combine(agentsDir, "kcap.json");
        var marker    = Path.Combine(agentsDir, KiroHooksInstaller.MarkerFileName);
        var userAgent = Path.Combine(agentsDir, "my-agent.json");
        await File.WriteAllTextAsync(kcapAgent, """
            {"name":"kcap","hooks":{"agentSpawn":[{"command":"kcap hook --kiro --event agentSpawn"}]}}
            """);
        await File.WriteAllTextAsync(marker, CapacitorVersion.Current());
        await File.WriteAllTextAsync(userAgent, """{"name":"my-agent"}""");

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(kcapAgent)).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(File.Exists(userAgent)).IsTrue();
        await Assert.That(KiroHooksInstaller.IsInstalled(kcapAgent)).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_codex_hooks() {
        // Regression: a user who installed via the pre-rename `kapacitor` CLI
        // has hooks.json entries pointing at `kapacitor codex-hook`. Running
        // `kcap uninstall` must clean those up — the rename PR did not migrate
        // the detection marker, so legacy entries previously survived.
        await using var fixture = await Fixture.CreateAsync();

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

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var codexRoot    = JsonNode.Parse(await File.ReadAllTextAsync(codexHooks))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_cursor_hooks() {
        // Mirror of the Codex legacy regression: a user who installed via the
        // pre-rename `kapacitor` CLI has cursor hooks.json entries pointing at
        // `kapacitor hook --cursor`. Uninstall must remove them while keeping
        // user-authored sibling entries intact.
        await using var fixture = await Fixture.CreateAsync();

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

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var cursorRoot   = JsonNode.Parse(await File.ReadAllTextAsync(cursorHooks))!.AsObject();
        var sessionStart = cursorRoot["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_claude_settings() {
        // Regression: a user who installed via the pre-rename `kapacitor` CLI
        // has `kapacitor@kapacitor` / `kapacitor@kurrent` in enabledPlugins and
        // a `kapacitor` marketplace entry. Uninstall must strip those while
        // preserving sibling settings.
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {
              "userKey": "keep",
              "enabledPlugins": {
                "kapacitor@kapacitor": true,
                "kapacitor@kurrent":   true,
                "other-plugin@vendor": true
              },
              "extraKnownMarketplaces": {
                "kapacitor": { "source": { "source": "directory", "path": "/legacy" } },
                "other":     { "source": { "source": "directory", "path": "/other" } }
              }
            }
            """);

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root         = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        var enabled      = root["enabledPlugins"]!.AsObject();
        var marketplaces = root["extraKnownMarketplaces"]!.AsObject();

        await Assert.That(enabled["kapacitor@kapacitor"]).IsNull();
        await Assert.That(enabled["kapacitor@kurrent"]).IsNull();
        await Assert.That(enabled["other-plugin@vendor"]!.GetValue<bool>()).IsTrue();

        await Assert.That(marketplaces["kapacitor"]).IsNull();
        await Assert.That(marketplaces["other"]).IsNotNull();

        await Assert.That(root["userKey"]!.GetValue<string>()).IsEqualTo("keep");
    }

    [Test]
    public async Task Keep_config_preserves_config_dir_but_removes_integrations() {
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {"enabledPlugins": {"kcap@kcap": true}}
            """);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"keep"}""");

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]).IsNull();

        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("keep");
    }

    [Test]
    public async Task Project_flag_strips_project_scope_entries() {
        await using var fixture = await Fixture.CreateAsync();

        // Fake project: a temp dir with a .git directory makes GitRepository.FindRoot
        // return it as the working tree root.
        using var tmp = new TempDir();
        tmp.CreateDir(".git");

        var projectClaude = tmp.PathTo(".claude", "settings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectClaude)!);
        await File.WriteAllTextAsync(projectClaude, """
            {
              "userLocal": "keep",
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var projectCodex = tmp.PathTo(".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectCodex)!);
        await File.WriteAllTextAsync(projectCodex, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var exit = await NewCommand(fixture, workdir: tmp.Path).HandleAsync(["uninstall", "--yes", "--project", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(projectClaude))!.AsObject();
        await Assert.That(claudeRoot["userLocal"]!.GetValue<string>()).IsEqualTo("keep");
        await Assert.That(claudeRoot["enabledPlugins"]!["kcap@kcap"]).IsNull();

        var codexRoot    = JsonNode.Parse(await File.ReadAllTextAsync(projectCodex))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
    }

    [Test]
    public async Task Project_flag_errors_when_not_inside_git_tree() {
        await using var fixture = await Fixture.CreateAsync();

        // A scratch dir with NO .git anywhere up the tree.
        using var tmp = new TempDir();

        var exit = await NewCommand(fixture, workdir: tmp.Path).HandleAsync(["uninstall", "--yes", "--project"]);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(fixture.Err.ToString()).Contains("--project requires a git working tree");
    }

    [Test]
    public async Task Marker_only_install_is_purged_even_when_json_has_no_kcap_entries() {
        // Regression for the "marker survives manual JSON edit" leak: the
        // upstream plugin removers delete the marker only when JSON entries
        // changed. If a user removed kcap entries by hand earlier, the
        // marker survives and IsInstalled keeps returning true. uninstall
        // must always nuke the markers.
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        // settings.json present but no kcap entries.
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), """{"userKey":"x"}""");
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        await File.WriteAllTextAsync(Path.Combine(codexDir, "hooks.json"), """{"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        await File.WriteAllTextAsync(Path.Combine(cursorDir, "hooks.json"), """{"version":1,"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(codexDir,  CodexHooksInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName))).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_purges_cursor_mcp_marker_even_when_json_has_no_kcap_entries() {
        // If a user hand-edited ~/.cursor/mcp.json to remove the kcap entries,
        // `plugin remove --cursor` still clears the sidecar ownership marker:
        // JsonMcpConfigWriter.Unregister clears it on any non-Failed outcome,
        // including when there were no entries left to remove. (The failure
        // path — where the marker is deliberately retained for retry — is
        // covered by User_level_uninstall_keeps_cursor_mcp_marker_when_unregister_fails.)
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var mcpPath = Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, """{"mcpServers":{}}""");

        var home   = fixture.UserHome;
        var marker = new McpMarker("cursor", home);
        marker.Record(mcpPath, ["kcap-review"]); // simulates a marker surviving a manual JSON edit
        await Assert.That(marker.Owned(mcpPath).ToArray()).IsNotEmpty();

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory, home: home).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(new McpMarker("cursor", home).Owned(mcpPath).ToArray()).IsEmpty();
    }

    [Test]
    public async Task User_level_uninstall_keeps_cursor_mcp_marker_when_unregister_fails() {
        // Regression (self-review): when ~/.cursor/mcp.json can't be
        // parsed/rewritten, JsonMcpConfigWriter.Unregister fails-closed and
        // deliberately RETAINS the ownership marker so a later retry can still
        // identify the kcap-* entries as kcap-owned. uninstall records the
        // failure but must NOT then wipe the marker — doing so would orphan
        // those entries (a retry would preserve them as user-authored). The
        // Cursor MCP marker is owned by `plugin remove --cursor`, not the sweep.
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var mcpPath = Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, "{ this is not valid json"); // → Unregister returns Failed

        var home   = fixture.UserHome;
        var marker = new McpMarker("cursor", home);
        marker.Record(mcpPath, ["kcap-review"]);
        await Assert.That(marker.Owned(mcpPath).ToArray()).IsNotEmpty();

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory, home: home).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsNotEqualTo(0); // the failed cursor MCP unregister propagates

        // Marker retained → a retry after the user fixes the file can still find + remove the kcap entries.
        await Assert.That(new McpMarker("cursor", home).Owned(mcpPath).ToArray()).IsNotEmpty();
    }

    [Test]
    public async Task Sweep_removes_kcap_prefixed_skill_folders_not_in_current_source_list() {
        // Regression for the "retired/renamed skill folder survives" leak:
        // AgentsSkillsInstaller.Remove uses the current SourceNames list, so
        // a kcap-* folder from an older release isn't matched. uninstall
        // must sweep the directory for our prefix to catch those.
        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);

        // A skill from the current list (handled by Remove) and a retired one
        // (only handled by the sweep). Plus a user-authored folder that must
        // survive both.
        var currentSkill = Path.Combine(skillsDir, $"kcap-{AgentsSkillsInstaller.SourceNames[0]}");
        var retiredSkill = Path.Combine(skillsDir, "kcap-retired-from-v0.42");
        var userSkill    = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(currentSkill);
        Directory.CreateDirectory(retiredSkill);
        Directory.CreateDirectory(userSkill);

        // Legacy Codex skills dir: same story.
        var legacyDir = Path.Combine(fixture.Home, ".codex", "skills");
        Directory.CreateDirectory(legacyDir);
        var legacyRetired = Path.Combine(legacyDir, "kcap-also-retired");
        Directory.CreateDirectory(legacyRetired);

        var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(Directory.Exists(currentSkill)).IsFalse();
        await Assert.That(Directory.Exists(retiredSkill)).IsFalse();
        await Assert.That(Directory.Exists(legacyRetired)).IsFalse();
        await Assert.That(Directory.Exists(userSkill)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Config_dir_is_preserved_when_user_level_steps_fail() {
        // Regression for the "discarded return codes" issue: when a step
        // returns non-zero, uninstall must NOT delete ~/.config/kcap —
        // doing so destroys the only state that lets the user re-run and
        // finish, and it would silently report success.
        //
        // Windows file ACLs would need an entirely different setup, so this
        // case is Unix-only. The aggregation logic is the same on every
        // platform; covering Unix is sufficient for regression purposes.
        await using var fixture = await Fixture.CreateAsync();

        // Force PluginCommand's Claude remove to fail by leaving a valid kcap
        // entry in a read-only directory: the atomic replace cannot create its
        // temp file there, so the edit reports Failed and the remover throws.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"enabledPlugins": {"kcap@kcap": true}}
            """);
        File.SetUnixFileMode(claudeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"survives-partial-failure"}""");

        try {
            var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("survives-partial-failure");
        } finally {
            // Restore write so the test fixture can be deleted.
            File.SetUnixFileMode(claudeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Cursor_hooks_write_failure_propagates_to_exit_code() {
        // Cursor's RemoveCursor previously returned 0 even when the helper's
        // inner try/catch swallowed a write failure. The helper now throws,
        // RemoveCursor catches + returns 1, and uninstall aggregates that
        // into hadFailures.
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var hooksPath = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);
        File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try {
            var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            // Failure path skips the config-dir delete so the user can re-run.
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Sweep_failure_propagates_to_exit_code() {
        // SweepCapacitorPrefixedDirs previously logged Directory.Delete errors
        // but never flipped hadFailures, so a stuck kcap-* folder under
        // ~/.agents/skills/ would leave uninstall claiming success.
        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(Path.Combine(skillsDir, "kcap-stuck"));

        // Strip write permission on the parent. Directory.Delete on a child
        // needs write+exec on the parent on every Unix; without write the
        // unlink fails with EACCES and Directory.Delete surfaces it.
        File.SetUnixFileMode(skillsDir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try {
            var exit = await NewCommand(fixture, workdir: AppContext.BaseDirectory).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            // Restore so the fixture can be cleaned up.
            File.SetUnixFileMode(skillsDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    static VendorAccount RegisterWiredClaudeAccount(AccountStore accounts, Fixture fixture, string dirName) {
        var plugin = fixture.Temp.CreateDir("plugin").Path;
        foreach (var n in AgentsSkillsInstaller.SourceNames)
            fixture.Temp.CreateFile(["plugin", "skills", n, "SKILL.md"], "---\nname: " + n + "\n---\n");

        var dir     = fixture.Temp.CreateDir(dirName).Path;
        var account = new VendorAccount(dirName, HarnessId.Claude, AccountDirectory.Normalize(dir), dirName, DateTimeOffset.UnixEpoch);

        accounts.Mutate(r => (r with { Accounts = [.. r.Accounts, account] }, 0));
        using (accounts.Lock()) AccountWiring.Wire(account, fixture.UserHome, new WiringOptions(plugin, fixture.Temp.PathTo(".agents", "skills"), () => "/usr/local/bin/kcap", NetworkAllowDomains: null), accounts);

        return account;
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Uninstall_removes_its_own_link() {
        await using var fixture = await Fixture.CreateAsync();

        var (links, link, binary) = LinkFixture(fixture);

        var exit = await NewCommand(fixture, links: links).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(new FileInfo(link).LinkTarget).IsNull();
        await Assert.That(File.Exists(binary)).IsTrue();
        await Assert.That(fixture.Out.ToString()).Contains($"Remove the kcap command link at {link}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Keep_path_links_leaves_it() {
        await using var fixture = await Fixture.CreateAsync();

        var (links, link, _) = LinkFixture(fixture);

        var exit = await NewCommand(fixture, links: links).HandleAsync(["uninstall", "--yes", "--keep-config", "--keep-path-links"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(new FileInfo(link).LinkTarget).IsNotNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_link_it_cannot_remove_fails_the_uninstall_with_a_sudo_hint() {
        await using var fixture = await Fixture.CreateAsync();

        var (links, link, _) = LinkFixture(fixture);
        var folder           = Path.GetDirectoryName(link)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            var exit = await NewCommand(fixture, links: links).HandleAsync(["uninstall", "--yes", "--keep-config"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(fixture.Err.ToString()).Contains($"sudo rm '{link}'");
        } finally {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Uninstall_removes_the_profile_line_with_its_link() {
        await using var fixture = await Fixture.CreateAsync();
        var (links, link) = LocalBinLinkFixture(fixture);
        var profile = Path.Combine(fixture.Home, ".zprofile");
        File.WriteAllText(profile, "export A=1\n");
        new ShellProfile(fixture.UserHome, "/bin/zsh").Add();

        var exit = await NewCommand(fixture, links: links).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(new FileInfo(link).LinkTarget).IsNull();
        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1\n");
        await Assert.That(fixture.Out.ToString()).Contains($"Remove the PATH line kcap added to {profile}");
    }

    /// The line also serves a link another install put there.
    [Test, ExcludeOn(OS.Windows)]
    public async Task Uninstall_keeps_the_profile_line_while_another_link_needs_it() {
        await using var fixture = await Fixture.CreateAsync();
        var (_, link) = LocalBinLinkFixture(fixture);
        var profile = Path.Combine(fixture.Home, ".zprofile");
        new ShellProfile(fixture.UserHome, "/bin/zsh").Add();
        var before = File.ReadAllText(profile);

        var exit = await NewCommand(fixture).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(new FileInfo(link).LinkTarget).IsNotNull();
        await Assert.That(File.ReadAllText(profile)).IsEqualTo(before);
    }

    static (KcapLinkRemover Links, string Link) LocalBinLinkFixture(Fixture f) {
        f.Temp.CreateFile("kcap-bin", "x");
        f.Temp.CreateDir(".local", "bin");

        var binary = f.Temp.PathTo("kcap-bin");
        var folder = ShellProfile.Folder(f.UserHome);
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, binary);

        return (new KcapLinkRemover([folder], binary), link);
    }

    static (KcapLinkRemover Links, string Link, string Binary) LinkFixture(Fixture f) {
        f.Temp.CreateFile("kcap-bin", "x");
        f.Temp.CreateDir("link-bin");

        var binary = f.Temp.PathTo("kcap-bin");
        var folder = f.Temp.PathTo("link-bin");
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, binary);

        return (new KcapLinkRemover([folder], binary), link, binary);
    }

    static WatcherPaths WatchersOf(Fixture f) => new(f.Root.Path("watchers"));

    /// What the daemon and watcher stop step reports reaches the command's own writers, the survivor
    /// that fails the uninstall included.
    [Test]
    public async Task A_watcher_the_stop_step_cannot_end_is_reported_on_the_commands_own_writers() {
        await using var fixture = await Fixture.CreateAsync();
        var paths = WatchersOf(fixture);
        Directory.CreateDirectory(paths.Directory);

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        int exit;
        // Held exclusively, the pid file can be neither read to stop the watcher nor deleted.
        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            exit = await NewCommand(fixture).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(fixture.Out.ToString()).Contains("No daemons are running.");
        await Assert.That(fixture.Out.ToString()).Contains("Could not stop watcher s-a");
        await Assert.That(fixture.Err.ToString()).Contains("Error killing watcher s-a");
        await Assert.That(fixture.Err.ToString()).Contains("Watcher s-a is still running.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Uninstall_without_systemd_keeps_the_config_while_a_daemon_unit_is_left() {
        await using var fixture = await Fixture.CreateAsync();
        var unit = SystemdUnit.UnitPath(fixture.UserHome, "laptop");
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        await File.WriteAllTextAsync(unit, "[Service]\nExecStart=/opt/kcap/kcap-daemon\n");

        var exit = await NewCommand(fixture, systemdRunning: () => false).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(fixture.Err.ToString()).Contains($"Could not remove daemon service 'laptop': systemd is not running. Delete {unit}");
        await Assert.That(File.Exists(unit)).IsTrue();
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test]
    [RunOn(OS.Linux), SupportedOSPlatform("linux")]
    public async Task Uninstall_without_systemd_keeps_the_config_when_the_unit_directory_is_unreadable() {
        await using var fixture = await Fixture.CreateAsync();
        var dir = Path.GetDirectoryName(SystemdUnit.UnitPath(fixture.UserHome, "laptop"))!;
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.None);

        try {
            var exit = await NewCommand(fixture, systemdRunning: () => false).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(fixture.Err.ToString()).Contains("Could not list daemon services");
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    const string ScopedConfig = """
        {"version":2,"active_profile":"default","profiles":{"default":{
          "excluded_paths":["/code/secret-project"],"excluded_repos":["acme/secret"],
          "allowed_paths":["/code"],"allowed_repos":["acme/open"]}}}
        """;

    [Test]
    public async Task The_prompt_lists_the_capture_exclusions_the_config_delete_loses() {
        await using var fixture = await Fixture.CreateAsync();
        var configPath = AppConfig.GetConfigPath(fixture.Root);
        await File.WriteAllTextAsync(configPath, ScopedConfig);

        var exit = await NewCommand(fixture, stdin: "n\n").HandleAsync(["uninstall"]);

        var output = fixture.Out.ToString();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Delete 4 capture exclusions");
        await Assert.That(output).Contains("ignored path /code/secret-project");
        await Assert.That(output).Contains("excluded repo acme/secret");
        await Assert.That(output).Contains("allowed path /code,");
        await Assert.That(output).Contains("allowed repo acme/open,");
        await Assert.That(output).Contains("kcap uninstall --keep-config");
        await Assert.That(output.IndexOf("ignored path", StringComparison.Ordinal)).IsLessThan(output.IndexOf("Proceed?", StringComparison.Ordinal));
        await Assert.That(await File.ReadAllTextAsync(configPath)).IsEqualTo(ScopedConfig);
    }

    [Test]
    public async Task Exclusions_past_ten_are_counted_and_named_by_profile() {
        await using var fixture = await Fixture.CreateAsync();
        var paths = string.Join(',', Enumerable.Range(1, 11).Select(i => $"\"/code/p{i:00}\""));
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(fixture.Root), $$$$"""
            {"version":2,"active_profile":"default","profiles":{
              "default":{"excluded_paths":[{{{{paths}}}}]},
              "work":{"excluded_repos":["acme/secret"]}}}
            """);

        var exit = await NewCommand(fixture).HandleAsync(["uninstall", "--yes"]);

        var output = fixture.Out.ToString();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Delete 12 capture exclusions");
        await Assert.That(output).Contains("ignored path /code/p10 (profile default)");
        await Assert.That(output).DoesNotContain("/code/p11");
        await Assert.That(output).Contains("and 2 more");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
    }

    [Test]
    public async Task Keep_config_keeps_the_exclusions_and_announces_no_loss() {
        await using var fixture = await Fixture.CreateAsync();
        var configPath = AppConfig.GetConfigPath(fixture.Root);
        await File.WriteAllTextAsync(configPath, ScopedConfig);

        var exit = await NewCommand(fixture).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fixture.Out.ToString()).DoesNotContain("capture exclusion");
        await Assert.That(ConfigMutator.LoadPure(configPath).Profiles["default"].ExcludedPaths).IsEquivalentTo(["/code/secret-project"]);
    }

    [Test]
    public async Task An_unreadable_config_is_announced_as_losing_whatever_exclusions_it_holds() {
        await using var fixture = await Fixture.CreateAsync();
        var configPath = AppConfig.GetConfigPath(fixture.Root);
        await File.WriteAllTextAsync(configPath, "{ not json");

        await NewCommand(fixture, stdin: "n\n").HandleAsync(["uninstall"]);

        var output = fixture.Out.ToString();
        await Assert.That(output).Contains($"Delete {configPath}, which kcap cannot read");
        await Assert.That(output).Contains("kcap uninstall --keep-config");
    }

    [Test]
    public async Task A_config_without_exclusions_adds_no_notice() {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(fixture.Root),
            """{"version":2,"active_profile":"default","profiles":{"default":{"server_url":"https://kcap.example.com"}}}""");

        await NewCommand(fixture, stdin: "n\n").HandleAsync(["uninstall"]);

        await Assert.That(fixture.Out.ToString()).DoesNotContain("capture exclusion");
    }
    /// <summary>A declined plan says that nothing happened, and the plan has said up front that kcap
    /// itself is not part of it.</summary>
    [Test]
    public async Task A_declined_plan_names_what_it_keeps_and_says_nothing_was_removed() {
        await using var fixture = await Fixture.CreateAsync();

        var exit = await NewCommand(fixture, stdin: "n\n").HandleAsync(["uninstall"]);

        var output = fixture.Out.ToString();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("It keeps kcap itself");
        await Assert.That(output.IndexOf("It keeps kcap itself", StringComparison.Ordinal))
            .IsLessThan(output.IndexOf("Proceed?", StringComparison.Ordinal));
        await Assert.That(output).Contains(UninstallCommand.CancelledLine);
    }

    [Test]
    [Arguments(InstallKind.Npm, "npm uninstall -g @kurrent/kcap")]
    [Arguments(InstallKind.App, "Uninstall the app")]
    [Arguments(InstallKind.Snap, "sudo snap remove kcap-desktop")]
    [Arguments(InstallKind.Script, "/home/u/.local/share/kcap. Delete that folder")]
    [Arguments(InstallKind.Unknown, "(/opt/kcap/kcap). Remove it the way it was installed.")]
    public async Task The_plan_says_how_to_remove_kcap_itself(InstallKind kind, string expected) {
        var line = UninstallCommand.KeptProgramLine(kind, "/home/u/.local/share/kcap", "kcap-desktop", "/opt/kcap/kcap");

        await Assert.That(line).Contains(expected);
    }

    UninstallCommand NewCommand(Fixture f, DaemonStore? store = null, string? workdir = null, UserHome? home = null, IOutgoingRecorders? recorders = null, ILocalStackFactory? stacks = null, KcapLinkRemover? links = null, Func<bool>? systemdRunning = null, string stdin = "") {
        var userHome = home ?? f.UserHome;
        var profiles = Resolutions.None(f.Root);
        var daemons  = store ?? Daemons.Store;
        var watchers = TestWatchers.In(WatchersOf(f), f.Root, profiles, new FixedCapacitorHttpClient());
        var harness  = TestHarnesses.Under(userHome);
        var console  = new StandardStreams(f.Out, f.Err, new StringReader(stdin));

        return new(
            daemons, f.Root, userHome, harness, TestBinaries.None, new AgentsPaths(userHome),
            new WorkingDirectory(workdir ?? AppContext.BaseDirectory),
            recorders ?? new OutgoingRecorders(daemons, f.Root, profiles, userHome, harness, TestBinaries.None, watchers, TimeProvider.System, console) {
                SystemdRunning = systemdRunning ?? (() => true),
            },
            stacks ?? new EmptyStackFactory(userHome, f.Root),
            links ?? new KcapLinkRemover([], ""),
            console);
    }

    sealed class EmptyStackFactory(UserHome home, ConfigRoot config) : ILocalStackFactory {
        public LocalRoot Root => LocalRoot.Resolve(null, home);

        public LocalStack Create() =>
            new(LocalRoot.Resolve(null, home), home, new FakeLocalServerService([], home), new HttpClient(), TimeProvider.System, new FakeOutgoingRecorders([]), config);

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    sealed class StackFactory(Func<LocalStack> create) : ILocalStackFactory {
        public LocalRoot Root => throw new InvalidOperationException("The command read the root without a platform refusal.");

        public LocalStack Create() => create();

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    sealed class UnsupportedStackFactory(LocalRoot root) : ILocalStackFactory {
        public LocalRoot Root => root;

        public LocalStack Create() => throw new LocalInstallFailure(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on macOS for now.");

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    /// <summary>A server another build installed holds its data under the config directory by default;
    /// a build that cannot manage it must not delete it along with the configuration.</summary>
    [Test]
    public async Task A_local_server_this_build_cannot_manage_survives_the_config_delete() {
        await using var fixture = await Fixture.CreateAsync();
        var root     = LocalRoot.Resolve(null, fixture.UserHome);
        var data     = Path.Combine(root.Directory, "data", "postgres", "PG_VERSION");
        var profiles = Path.Combine(fixture.ConfigDir, "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        await File.WriteAllTextAsync(data, "18");
        await File.WriteAllTextAsync(profiles, "{}");

        var exit = await NewCommand(fixture, stacks: new UnsupportedStackFactory(root)).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(data)).IsTrue();
        await Assert.That(File.Exists(profiles)).IsFalse();
        await Assert.That(fixture.Out.ToString()).Contains($"Keep the local server in {root.Directory}");
    }

    [Test]
    public async Task An_empty_local_root_goes_with_the_config_directory_where_no_server_runs() {
        await using var fixture = await Fixture.CreateAsync();
        var root = LocalRoot.Resolve(null, fixture.UserHome);
        Directory.CreateDirectory(root.Directory);

        var exit = await NewCommand(fixture, stacks: new UnsupportedStackFactory(root)).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
        await Assert.That(fixture.Out.ToString()).DoesNotContain("Keep the local server");
    }

    const string LocalUrl = "http://localhost:9473";

    sealed record LocalServer(LocalRoot Root, FakeLocalServerService Service, List<string> Log, ILocalStackFactory Stacks, FakeOutgoingRecorders Recorders) {
        public string Dir(string name) => Path.Combine(Root.Directory, name);

        public LocalServerUnit Unit => LocalServerUnit.ForInstallation(Root, 9473);
    }

    static LocalServer InstallLocalServer(Fixture f) {
        var log     = new List<string>();
        var root    = LocalRoot.Resolve(null, f.UserHome);
        var service = new FakeLocalServerService(log, f.UserHome);

        root.EnsureCreated();
        LocalStateFiles.PublishRegistry(root, InstallRegistry.Empty.WithActive(new("0123456789abcdef0123456789abcdef", 9473, LocalUrl, "1.2.3")));
        service.Install(LocalServerUnit.ForInstallation(root, 9473));
        Directory.CreateDirectory(Path.Combine(root.Directory, "data"));

        var watchers = f.Temp.PathTo("watchers");
        var stacks   = new StackFactory(() => new(root, f.UserHome, service, new HttpClient(), TimeProvider.System, new FakeOutgoingRecorders(log, watchers), f.Root));

        return new(root, service, log, stacks, new FakeOutgoingRecorders(log, watchers));
    }

    async Task<(int Exit, string Out, string Err)> UninstallWithLocalAsync(Fixture f, LocalServer local, string stdin, params string[] args) {
        var exit = await NewCommand(f, recorders: local.Recorders, stacks: local.Stacks, stdin: stdin).HandleAsync(["uninstall", .. args]);

        return (exit, f.Out.ToString(), f.Err.ToString());
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Yes_alone_refuses_when_a_local_server_is_installed() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);

        var (exit, _, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("--purge-local");
        await Assert.That(local.Service.IsInstalled(local.Unit)).IsTrue();
        await Assert.That(local.Log).DoesNotContain("recorders:stop");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Purge_local_purges_before_the_config_directory_goes() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);

        var (exit, output, _) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--purge-local");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Purged the local server");
        await Assert.That(local.Service.IsInstalled(local.Unit)).IsFalse();
        // Ahead of every other step, the uninstall's own daemon stop included.
        await Assert.That(local.Log.IndexOf("uninstall")).IsLessThan(local.Log.LastIndexOf("recorders:stop"));
        await Assert.That(Directory.EnumerateFileSystemEntries(fixture.ConfigDir).Select(path => Path.GetFileName(path)!).ToArray())
            .IsEquivalentTo(new[] { "local" });
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Keep_config_keeps_the_local_data() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);

        var (exit, _, _) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--keep-config");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(local.Log).Contains("uninstall");
        await Assert.That(local.Service.IsInstalled(local.Unit)).IsFalse();
        await Assert.That(Directory.Exists(local.Dir("data"))).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Interactive_prompt_names_the_deletion() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);

        var result = await UninstallWithLocalAsync(fixture, local, "y\n");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.Out).Contains(LocalUrl);
        await Assert.That(result.Out).Contains("every session it recorded");
        await Assert.That(result.Out).Contains("Purged the local server");
        await Assert.That(Directory.Exists(local.Dir("data"))).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Unreadable_local_registry_keeps_the_config_directory() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        await File.WriteAllTextAsync(local.Root.RegistryPath, "{ not json");
        var kcapTs = fixture.Temp.CreateFile(".pi/agent/extensions/kcap.ts", "export default function(pi){}");

        var (exit, _, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--purge-local");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains(local.Root.RegistryPath);
        await Assert.That(local.Log).DoesNotContain("uninstall");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(File.Exists(kcapTs)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_local_removal_that_fails_exits_1_and_keeps_the_config_directory() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        local.Service.StopFailure      = new LocalLifecycleException("launchctl bootout failed");
        local.Service.UninstallFailure = new LocalLifecycleException("launchctl bootout failed");

        var (exit, _, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--purge-local");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("launchctl bootout failed");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Yes_alone_refuses_when_a_local_change_was_interrupted() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        LocalStateFiles.WriteJournal(local.Root, new("setup", "pending", ServerStart.Deny, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        var (exit, _, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("interrupted");
        await Assert.That(errors).Contains("--purge-local");
        await Assert.That(local.Service.IsInstalled(local.Unit)).IsTrue();
        await Assert.That(local.Log).DoesNotContain("recorders:stop");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Keep_config_still_refuses_an_interrupted_purge_without_purge_local() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        LocalStateFiles.WriteJournal(local.Root, new(LocalPurgeJournal.Operation, LocalPurgeJournal.Retiring, ServerStart.Deny, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        var (exit, _, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--keep-config");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("finish deleting the local server's data");
        await Assert.That(errors).Contains("--purge-local");
        await Assert.That(local.Service.IsInstalled(local.Unit)).IsTrue();
        await Assert.That(Directory.Exists(local.Dir("data"))).IsTrue();
        await Assert.That(local.Log).DoesNotContain("recorders:stop");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_local_removal_that_throws_an_io_error_still_finishes_the_rest() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        using var fault = FaultPoints.Arm(point => {
            if (point == "purge:after-data") throw new IOException("disk went away");
        });

        var (exit, output, errors) = await UninstallWithLocalAsync(fixture, local, "", "--yes", "--purge-local");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("Could not remove the local server: disk went away");
        await Assert.That(errors).Contains("kcap uninstall finished with errors");
        await Assert.That(output).DoesNotContain("kcap uninstalled.");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test]
    public async Task No_local_server_needs_no_purge_flag() {
        await using var fixture = await Fixture.CreateAsync();

        var exit = await NewCommand(fixture, recorders: new FakeOutgoingRecorders([])).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(fixture.Home, ".vibe"))).IsFalse();
    }

    [Test]
    public async Task Busy_daemon_refuses_and_changes_nothing() {
        await using var fixture = await Fixture.CreateAsync();
        var log       = new List<string>();
        var recorders = new FakeOutgoingRecorders(log);
        recorders.Busy.Add("daemon 'x' is running or starting agents");

        var exit = await NewCommand(fixture, recorders: recorders).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(fixture.Err.ToString()).Contains("--force");
        await Assert.That(log).DoesNotContain("recorders:stop");
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test]
    public async Task Force_proceeds_past_a_busy_daemon() {
        await using var fixture = await Fixture.CreateAsync();
        var log       = new List<string>();
        var recorders = new FakeOutgoingRecorders(log);
        recorders.Busy.Add("daemon 'x' is running or starting agents");

        var exit = await NewCommand(fixture, recorders: recorders).HandleAsync(["uninstall", "--yes", "--force"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(log).Contains("recorders:stop");
    }

    Task<int> RunUninstall(Fixture fixture, string? workdir = null, DaemonStore? store = null, params string[] args) =>
        NewCommand(fixture, store, workdir).HandleAsync(["uninstall", "--yes", .. args]);

    [Test]
    public async Task Uninstall_unwires_every_registered_account_and_deletes_the_registry() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        var work     = RegisterWiredClaudeAccount(accounts, fixture, ".claude-work");
        var personal = RegisterWiredClaudeAccount(accounts, fixture, ".claude-personal");

        var exit = await RunUninstall(fixture);

        await Assert.That(exit).IsEqualTo(0);
        foreach (var a in new[] { work, personal }) {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(AccountLayouts.Claude(fixture.UserHome, a.Directory).UserSettings))!.AsObject();
            await Assert.That(root["enabledPlugins"]?["kcap@kcap"]).IsNull();
        }
        await Assert.That(Directory.Exists(accounts.Directory)).IsFalse();
    }

    [Test]
    public async Task Uninstall_restores_the_default_directory_status_line() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        var settings = fixture.Temp.CreateFile(".claude/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        var adopted  = AccountAdoption.EnsureDefault(accounts, HarnessId.Claude, fixture.Temp.PathTo(".claude"), TimeProvider.System);
        using (accounts.Lock()) AccountWiring.WireStatusLine(adopted.Id, fixture.UserHome, accounts);
        await Assert.That(ClaudeStatusLineCommand.IsOurs(JsonNode.Parse(await File.ReadAllTextAsync(settings))!["statusLine"]!["command"]!.GetValue<string>())).IsTrue();

        var exit = await RunUninstall(fixture);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(settings))!["statusLine"]!["command"]!.GetValue<string>()).IsEqualTo("echo mine");
    }

    [Test]
    public async Task Keep_config_still_deletes_the_account_registry() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        RegisterWiredClaudeAccount(accounts, fixture, ".claude-work");

        var exit = await RunUninstall(fixture, args: "--keep-config");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(accounts.Directory)).IsFalse();
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Uninstall_deletes_the_daemon_state_directory(bool keepConfig) {
        await using var fixture = await Fixture.CreateAsync();
        var store = new DaemonStore(DaemonStore.DefaultDirectory(fixture.UserHome));
        Directory.CreateDirectory(store.StateDirectory("vscode"));
        await File.WriteAllTextAsync(store.LockPath("vscode"), "x");
        await File.WriteAllTextAsync(Path.Combine(store.StateDirectory("vscode"), "consent.json"), "{}");

        var exit = await RunUninstall(fixture, store: store, args: keepConfig ? ["--keep-config"] : []);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(store.Directory)).IsFalse();
    }

    /// <summary>Pins that a default daemons directory which is a link is never cleaned: its target could
    /// be anywhere.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task Uninstall_leaves_a_linked_daemons_directory() {
        await using var fixture = await Fixture.CreateAsync();
        var store = new DaemonStore(DaemonStore.DefaultDirectory(fixture.UserHome));
        Directory.CreateDirectory(Path.GetDirectoryName(store.Directory)!);
        Directory.CreateSymbolicLink(store.Directory, Daemons.Directory);
        var lockFile = Daemons.CreateFile("vscode.lock", "x");

        var exit = await RunUninstall(fixture, store: store);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(lockFile)).IsTrue();
    }

    /// <summary>Pins that a daemons directory KCAP_DAEMONS_DIR moved is never cleaned: it may be shared,
    /// so nothing in it can be taken for kcap's.</summary>
    [Test]
    public async Task Uninstall_leaves_a_daemons_directory_the_environment_moved() {
        await using var fixture = await Fixture.CreateAsync();
        var lockFile = Daemons.CreateFile("vscode.lock", "x");

        var exit = await RunUninstall(fixture);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(lockFile)).IsTrue();
    }

    [Test]
    public async Task Uninstall_keeps_the_registry_when_an_account_cannot_be_unwired() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        var broken   = RegisterWiredClaudeAccount(accounts, fixture, ".claude-broken");
        await File.WriteAllTextAsync(AccountLayouts.Claude(fixture.UserHome, broken.Directory).UserSettings, "{ not json");

        var exit = await RunUninstall(fixture);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(accounts.Directory, "accounts.json"))).IsTrue();
        await Assert.That(Directory.Exists(Daemons.Directory)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Uninstall_keeps_an_unreadable_registry_and_reports_the_uninstall_incomplete() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        RegisterWiredClaudeAccount(accounts, fixture, ".claude-work");
        var registry = Path.Combine(accounts.Directory, "accounts.json");
        File.SetUnixFileMode(registry, UnixFileMode.None);

        try {
            var exit = await RunUninstall(fixture);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(File.Exists(registry)).IsTrue();
        } finally {
            File.SetUnixFileMode(registry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Uninstall_deletes_an_unparseable_registry() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        RegisterWiredClaudeAccount(accounts, fixture, ".claude-work");
        await File.WriteAllTextAsync(Path.Combine(accounts.Directory, "accounts.json"), "{ not json");

        var exit = await RunUninstall(fixture);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(accounts.Directory)).IsFalse();
    }

    [Test]
    public async Task Project_scope_uninstall_also_deletes_the_account_registry() {
        await using var fixture = await Fixture.CreateAsync();
        var accounts = AccountStore.Beside(Daemons.Store);
        RegisterWiredClaudeAccount(accounts, fixture, ".claude-work");

        using var tmp = new TempDir();
        tmp.CreateDir(".git");

        var exit = await RunUninstall(fixture, tmp.Path, args: "--project");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(accounts.Directory)).IsFalse();
    }

    [Test]
    public async Task Uninstall_removes_vibe_hooks_and_mcp_but_preserves_user_settings() {
        await using var fixture = await Fixture.CreateAsync();
        var vibe = TestHarnesses.Under(fixture.UserHome).Of<Capacitor.Cli.Core.Harness.MistralVibe.MistralVibeHarness>().Paths;
        Directory.CreateDirectory(vibe.Home);
        await File.WriteAllTextAsync(vibe.ConfigToml, "model = \"user-model\"\n");
        Capacitor.Cli.Core.Harness.MistralVibe.MistralVibeHooksInstaller.Install(vibe.HooksToml);
        Capacitor.Cli.Core.Harness.MistralVibe.MistralVibeConfigToml.RegisterKcapMcpServers(vibe.ConfigToml);

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(0);
        await Assert.That(Capacitor.Cli.Core.Harness.MistralVibe.MistralVibeHooksInstaller.IsInstalled(vibe.HooksToml)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(vibe.ConfigToml)).Contains("user-model");
        await Assert.That(Capacitor.Cli.Core.Harness.MistralVibe.MistralVibeConfigToml.OwnsAnything(vibe.ConfigToml)).IsFalse();
    }

    [Test]
    public async Task Uninstall_removes_cache_legacy_cursor_spool_and_retired_vendor_skills() {
        await using var fixture = await Fixture.CreateAsync();
        var cache = fixture.Temp.CreateDir(".cache", "kcap").Path;
        var spool = fixture.Temp.CreateDir(".cursor", "kcap-pending").Path;
        var kiroSkill = fixture.Temp.CreateDir(".kiro", "skills", "kcap-retired").Path;
        var agySkill = fixture.Temp.CreateDir(".gemini", "skills", "kcap-retired").Path;
        var userCache = fixture.Temp.CreateDir(".cache", "user-app").Path;
        await File.WriteAllTextAsync(Path.Combine(cache, "opencode-imported.json"), "{}");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(0);
        foreach (var path in new[] { cache, spool, kiroSkill, agySkill })
            await Assert.That(Directory.Exists(path)).IsFalse();
        await Assert.That(Directory.Exists(userCache)).IsTrue();
    }

    [Test]
    public async Task Uninstall_resets_a_kiro_default_left_naming_a_missing_kcap_agent() {
        await using var fixture = await Fixture.CreateAsync();
        var kiro = TestHarnesses.Under(fixture.UserHome).Of<KiroHarness>().Paths;
        fixture.Temp.CreateFile(Path.GetRelativePath(fixture.Home, kiro.SettingsFile), "{\"chat.defaultAgent\": \"kcap\"}");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(0);
        await Assert.That(KiroSettings.ReadDefaultAgent(kiro.SettingsFile)).IsEqualTo("kiro_default");
    }

    [Test]
    public async Task Uninstall_retains_kiro_restore_marker_when_settings_cannot_be_read() {
        await using var fixture = await Fixture.CreateAsync();
        var kiro = TestHarnesses.Under(fixture.UserHome).Of<KiroHarness>().Paths;
        Directory.CreateDirectory(Path.GetDirectoryName(kiro.KcapAgentJson)!);
        Directory.CreateDirectory(Path.GetDirectoryName(kiro.SettingsFile)!);
        await File.WriteAllTextAsync(kiro.KcapAgentJson, "{}");
        await File.WriteAllTextAsync(kiro.SettingsFile, "{broken");
        KiroHooksInstaller.WriteMarker(kiro.KcapAgentJson, "my-original-agent");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(1);
        await Assert.That(KiroHooksInstaller.ReadPreviousDefault(kiro.KcapAgentJson)).IsEqualTo("my-original-agent");
        await Assert.That(File.Exists(kiro.KcapAgentJson)).IsTrue();
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
    }

    [Test]
    public async Task Uninstall_reports_malformed_antigravity_hooks_instead_of_claiming_success() {
        await using var fixture = await Fixture.CreateAsync();
        var agy = TestHarnesses.Under(fixture.UserHome).Of<Capacitor.Cli.Core.Harness.Antigravity.AntigravityHarness>().Paths;
        Capacitor.Cli.Core.Harness.Antigravity.AntigravityHooksInstaller.Install(agy.GlobalHooksJson);
        await File.WriteAllTextAsync(agy.GlobalHooksJson, "{broken");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(1);
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(agy.GlobalHooksJson)).IsEqualTo("{broken");
        await Assert.That(Capacitor.Cli.Core.Harness.Antigravity.AntigravityHooksInstaller.IsInstalled(agy.GlobalHooksJson)).IsTrue();
    }

    [Test]
    public async Task Uninstall_reports_an_unreadable_git_config_and_preserves_retry_state() {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Home, ".gitconfig"), "[hook ");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(1);
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Home, ".gitconfig"))).IsEqualTo("[hook ");
    }

    sealed class ArrivingSetup(Func<Task<IReadOnlyList<string>>> busy) : IOutgoingRecorders {
        public string WatcherDirectory => "/watchers";
        public Task<IReadOnlyList<string>> BusyAsync() => busy();
        public Task<bool> StopAsync() => throw new InvalidOperationException("Uninstall stopped recorders without local-data consent.");
        public IReadOnlyList<string> Live() => [];
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_setup_after_inspection_cannot_bypass_local_data_consent() {
        await using var fixture = await Fixture.CreateAsync();
        LocalServer? arrived = null;
        var recorders = new ArrivingSetup(() => {
            arrived = InstallLocalServer(fixture);
            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        var exit = await NewCommand(fixture, recorders: recorders).HandleAsync(["uninstall", "--yes"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Directory.Exists(arrived!.Dir("data"))).IsTrue();
        await Assert.That(File.Exists(arrived.Root.RegistryPath)).IsTrue();
    }

    /// <summary>Keeping data is consent to remove only the server the prompt named, not one that arrived after it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_setup_after_inspection_is_not_removed_under_keep_config() {
        await using var fixture = await Fixture.CreateAsync();
        LocalServer? arrived = null;
        var recorders = new ArrivingSetup(() => {
            arrived = InstallLocalServer(fixture);
            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        var exit = await NewCommand(fixture, recorders: recorders).HandleAsync(["uninstall", "--yes", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(arrived!.Service.IsInstalled(arrived.Unit)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_purge_started_after_inspection_still_requires_consent_before_recovery(bool skipPrompt) {
        await using var fixture = await Fixture.CreateAsync();
        LocalServer? arrived = null;
        var recorders = new ArrivingSetup(async () => {
            arrived = InstallLocalServer(fixture);
            using var lease = await LifecycleLease.AcquireAsync(arrived.Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
            var installation = ((StateRead<InstallRegistry>.Present)lease.ReadRegistry()).Value.Active!;
            LocalPurge.Begin(lease, installation, fixture.ConfigDir, arrived.Recorders.WatcherDirectory);
            return [];
        });

        var exit = await NewCommand(fixture, recorders: recorders, stdin: "y\n").HandleAsync(skipPrompt
            ? ["uninstall", "--yes", "--keep-config"] : ["uninstall", "--keep-config"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Directory.Exists(arrived!.Dir("data"))).IsTrue();
        await Assert.That(File.Exists(arrived.Root.JournalPath)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Orphaned_local_files_are_not_deleted_without_an_installation_registry() {
        await using var fixture = await Fixture.CreateAsync();
        var data = fixture.Temp.CreateDir(".config", "kcap", "local", "data").Path;
        await File.WriteAllTextAsync(Path.Combine(data, "recording"), "keep");

        await Assert.That(await RunUninstall(fixture)).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(data, "recording"))).IsEqualTo("keep");
    }

    sealed class ObservingRecorders(IOutgoingRecorders inner, Action observe) : IOutgoingRecorders {
        public string WatcherDirectory => inner.WatcherDirectory;
        public IReadOnlyList<string> Live() => inner.Live();
        public Task<IReadOnlyList<string>> BusyAsync() => inner.BusyAsync();
        public async Task<bool> StopAsync() {
            var stopped = await inner.StopAsync();
            observe();
            return stopped;
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Config_cleanup_keeps_the_lifecycle_lease_and_retired_origin() {
        await using var fixture = await Fixture.CreateAsync();
        var local = InstallLocalServer(fixture);
        var heldDuringCleanup = false;
        var recorders = new ObservingRecorders(local.Recorders, () => {
            if (local.Service.IsInstalled(local.Unit)) return;
            try { using var competing = new FileStream(local.Root.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { heldDuringCleanup = true; }
        });
        await File.WriteAllTextAsync(fixture.Root.Path("token.json"), "user token");

        var exit = await NewCommand(fixture, recorders: recorders, stacks: local.Stacks)
            .HandleAsync(["uninstall", "--yes", "--purge-local"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(heldDuringCleanup).IsTrue();
        await Assert.That(File.Exists(fixture.Root.Path("token.json"))).IsFalse();
        await Assert.That(File.Exists(local.Root.LockPath)).IsTrue();
        await Assert.That(LocalEndpoints.Read(local.Root).Classify(LocalUrl)).IsTypeOf<LocalEndpointClass.Retired>();
        await Assert.That(Directory.Exists(local.Dir("data"))).IsFalse();
    }

    sealed class Fixture : IAsyncDisposable {
        TempHome? _tempHome;

        public required string Home      { get; init; }
        public required string ConfigDir { get; init; }

        public TempHome Temp => _tempHome!;

        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();

        public ConfigRoot Root     => new(ConfigDir);
        public UserHome   UserHome => new(Home);

        public static Task<Fixture> CreateAsync() {
            var tmp = new TempHome();
            var configDir = tmp.CreateDir(".config", "kcap").Path;

            return Task.FromResult(new Fixture {
                Home      = tmp.Path,
                ConfigDir = configDir,
                _tempHome = tmp
            });
        }

        public ValueTask DisposeAsync() {
            _tempHome?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
