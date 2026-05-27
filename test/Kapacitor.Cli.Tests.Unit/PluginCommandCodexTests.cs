using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class PluginCommandCodexTests {
    [Test]
    public async Task InstallCodexHooks_writes_all_six_events() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]!.AsObject();

        foreach (var evt in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop" }) {
            await Assert.That(hooks[evt]).IsNotNull();
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);

            var inner           = entries[0]!["hooks"]!.AsArray();
            var expectedTimeout = evt == "PermissionRequest" ? 86400 : 30;
            await Assert.That(inner[0]!["type"]!.GetValue<string>()).IsEqualTo("command");
            await Assert.That(inner[0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor codex-hook");
            await Assert.That(inner[0]!["timeout"]!.GetValue<int>()).IsEqualTo(expectedTimeout);
        }
    }

    [Test]
    public async Task PermissionRequest_hook_uses_long_timeout_so_dashboard_decision_isnt_killed() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);

        var root             = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var permissionEntries = root["hooks"]!["PermissionRequest"]!.AsArray();
        var kapacitorEntry = permissionEntries.First(e =>
            (e!["hooks"] as JsonArray)!.Any(h =>
                h?["command"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("kapacitor codex-hook"))
        );
        var timeout = kapacitorEntry!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>();

        // 86400 = 24h; must be >= 86400 so Codex cannot kill the hook
        // before the dashboard sends back an approval or denial.
        await Assert.That(timeout).IsGreaterThanOrEqualTo(86400);
    }

    [Test]
    public async Task InstallCodexHooks_preserves_other_top_level_keys() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """{"some_other_setting": true}""");

        PluginCommand.InstallCodexHooks(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root["some_other_setting"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["hooks"]).IsNotNull();
    }

    [Test]
    public async Task InstallCodexHooks_overwrites_existing_kapacitor_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        PluginCommand.InstallCodexHooks(path);

        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();

        var commands = sessionStart
            .SelectMany(e => e!["hooks"]!.AsArray())
            .Select(h => h!["command"]!.GetValue<string>())
            .ToList();

        await Assert.That(commands).Contains("kapacitor codex-hook");
        await Assert.That(commands).Contains("/usr/local/bin/other");
        await Assert.That(commands.Count(c => c == "kapacitor codex-hook")).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveCodexHooks_clears_all_kapacitor_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);
        var ok = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]?.AsObject();

        if (hooks is not null) {
            foreach (var (_, value) in hooks) {
                var commands = value!.AsArray()
                    .SelectMany(e => e!["hooks"]!.AsArray())
                    .Select(h => h!["command"]!.GetValue<string>());

                foreach (var cmd in commands) {
                    await Assert.That(cmd).DoesNotContain("kapacitor codex-hook");
                }
            }
        }
    }

    // Fix #2: non-string `command` field should not throw — treated as non-match.

    [Test]
    public async Task InstallCodexHooks_tolerates_numeric_command_field_in_existing_entry() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Entry whose inner hook has `command: 42` (a number, not a string).
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] }
                ]
              }
            }
            """);

        // Should not throw and should return true.
        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        // The malformed entry (non-string command) must be preserved as a
        // non-kapacitor entry, and the kapacitor entry must also appear.
        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(2); // preserved + kapacitor
    }

    [Test]
    public async Task RemoveCodexHooks_tolerates_numeric_command_field_in_existing_entry() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Mix: a malformed entry (number command) and a real kapacitor entry.
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var ok = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(ok).IsTrue();

        // Malformed entry must be preserved; kapacitor entry removed.
        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_for_numeric_command() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":42}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_true_for_matching_string_command() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_for_null() {
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(null)).IsFalse();
    }

    // ---- Agent skill install / remove (via AgentsSkillsInstaller) ----

    [Test]
    public async Task Install_copies_known_skills_with_kapacitor_prefix() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsTrue();

        var recap  = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap",  "SKILL.md"));
        var errors = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-errors", "SKILL.md"));
        await Assert.That(recap).Contains("recap body");
        await Assert.That(errors).Contains("errors body");
    }

    [Test]
    public async Task Install_copies_all_five_known_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsTrue();

        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            var path = Path.Combine(target, $"kapacitor-{name}", "SKILL.md");
            await Assert.That(File.Exists(path)).IsTrue();
        }
    }

    [Test]
    public async Task SourceNames_contains_expected_five() {
        var expected = new[] {
            "recap",
            "errors",
            "hide",
            "disable",
            "validate-plan"
        };

        await Assert.That(AgentsSkillsInstaller.SourceNames).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Install_returns_false_when_known_folder_missing() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");

        // Write four of the five expected names — leave validate-plan missing.
        WriteSkill(source, "recap",   "r");
        WriteSkill(source, "errors",  "e");
        WriteSkill(source, "hide",    "h");
        WriteSkill(source, "disable", "d");

        // Pre-existing target folder for one of the known skills. The preflight
        // must NOT delete it because the install is aborted before any destructive
        // step runs.
        WriteSkill(target, "kapacitor-recap", "stale recap that must survive");

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsFalse();

        // Pre-existing target folder unchanged — preflight bailed before deletion.
        var preserved = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
        await Assert.That(preserved).IsEqualTo("stale recap that must survive");

        // None of the other expected target folders were created.
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-errors"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-hide"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-disable"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-validate-plan"))).IsFalse();
    }

    [Test]
    public async Task Install_overwrites_existing_kapacitor_skill() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{(name == "recap" ? "new recap" : $"{name} body")}");
        }

        // Pre-existing stale copy in target.
        WriteSkill(target, "kapacitor-recap", "stale recap");

        AgentsSkillsInstaller.Install(source, target);

        var recap = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
        await Assert.That(recap).Contains("new recap");
    }

    [Test]
    public async Task Install_preserves_foreign_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        // Foreign skill the user installed themselves — must not be touched.
        WriteSkill(target, "user-skill", "user content");

        AgentsSkillsInstaller.Install(source, target);

        var foreign = await File.ReadAllTextAsync(Path.Combine(target, "user-skill", "SKILL.md"));
        await Assert.That(foreign).IsEqualTo("user content");
    }

    [Test]
    public async Task Install_returns_false_for_missing_source() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "nonexistent");
        var       target = Path.Combine(tmp.Path, "agents-skills");

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Remove_deletes_known_skills_only() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "agents-skills");
        WriteSkill(target, "kapacitor-recap",  "recap");
        WriteSkill(target, "kapacitor-errors", "errors");
        WriteSkill(target, "user-skill",       "user content");

        var result = AgentsSkillsInstaller.Remove(target);
        await Assert.That(result.RemovedAny).IsTrue();

        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-recap"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-errors"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, "user-skill", "SKILL.md"))).IsTrue();
    }

    [Test]
    public async Task Remove_returns_false_when_nothing_to_remove() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "agents-skills");
        Directory.CreateDirectory(target);

        var result = AgentsSkillsInstaller.Remove(target);
        await Assert.That(result.RemovedAny).IsFalse();
    }

    static void WriteSkill(string root, string name, string body) {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}

// Separated into its own class so it joins two serialization groups:
//   class-level: "HomeEnvVarMutation" — prevents concurrent HOME-mutating tests
//     from leaking the real user profile into our PluginCommand call.
//   method-level: CodexHookCommandTests.ConsoleSerialGroup ("CodexHookCommandTests.Console")
//     — TUnit's NotInParallel takes a single key per attribute, so we must share
//     the SAME literal token that every Console.Out-redirecting test in
//     CodexHookCommandTests uses. A distinct "Console.Out_redirect" token would
//     leave us racing against CodexHookCommandTests on the process-wide
//     Console.Out writer.
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandCodexInstallIntegrationTests {
    // SetupCommand.ResolvePluginPath probes paths relative to Environment.ProcessPath.
    // In the test runner the process exe lives at
    //   <repo>/test/Kapacitor.Cli.Tests.Unit/bin/Debug/net10.0/Kapacitor.Cli.Tests.Unit
    // None of the resolver's fallbacks exist by default, so this helper plants
    // a fake plugin tree at the "<exeDir>/../../kapacitor" path the resolver
    // probes (= "<bin>/kapacitor"). The folder is cleaned up after the test
    // so it doesn't pollute subsequent runs that expect resolution to fail.
    static string ProbedPluginRoot() {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor"));
    }

    static void PlantFakePlugin(string pluginRoot) {
        var skillsSrc = Path.Combine(pluginRoot, "skills");
        Directory.CreateDirectory(skillsSrc);

        // AgentsSkillsInstaller.Install has a per-skill preflight that requires
        // every name in AgentsSkillsInstaller.SourceNames to be present. Plant a
        // stub SKILL.md so the copy succeeds.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            var dir = Path.Combine(skillsSrc, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
        }
    }

    static void RemoveFakePlugin(string pluginRoot) {
        try { Directory.Delete(pluginRoot, recursive: true); } catch { /* best effort */ }
    }

    [Test, NotInParallel("CodexHookCommandTests.Console")]
    public async Task InstallCodex_prints_hooks_trust_hint_after_success() {
        using var tmp = new TempDir();

        // PathHelpers.HomeDirectory reads HOME first and falls back to UserProfile
        // (USERPROFILE on Windows). On Windows shells that export HOME (Git Bash,
        // MSYS, Cygwin, WSL) the production path resolver picks HOME, so setting
        // only USERPROFILE would leave the test writing to the real ~/.codex.
        // Set BOTH and restore BOTH regardless of OS.
        var originalHome        = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME",        tmp.Path);
        Environment.SetEnvironmentVariable("USERPROFILE", tmp.Path);

        var pluginRoot = ProbedPluginRoot();
        PlantFakePlugin(pluginRoot);

        var capturedOut = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(capturedOut);

        try {
            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"]);
            await Assert.That(exit).IsEqualTo(0);

            var stdout = capturedOut.ToString();
            await Assert.That(stdout).Contains("/hooks");
            await Assert.That(stdout).Contains("trust");
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("HOME",        originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            RemoveFakePlugin(pluginRoot);
        }
    }

    // AI-698 regression: when the top-level skills/ folder is present but one
    // or more individual skill sub-folders are missing (packaging defect),
    // `plugin install --codex` must fail BEFORE writing hooks. The per-skill
    // preflight must run before InstallCodexHooks to maintain the atomicity
    // guarantee from AI-676 — either everything installs or nothing does.
    [Test, NotInParallel("CodexHookCommandTests.Console")]
    public async Task InstallCodex_fails_before_writing_hooks_when_individual_skill_folder_missing() {
        using var tmp = new TempDir();

        var originalHome        = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME",        tmp.Path);
        Environment.SetEnvironmentVariable("USERPROFILE", tmp.Path);

        var pluginRoot = ProbedPluginRoot();

        // Plant a fake plugin tree with skills/ present but validate-plan/ missing.
        // This simulates a packaging defect where the top-level folder exists but
        // at least one individual skill folder is absent.
        var skillsSrc = Path.Combine(pluginRoot, "skills");
        Directory.CreateDirectory(skillsSrc);
        foreach (var name in AgentsSkillsInstaller.SourceNames.Where(n => n != "validate-plan")) {
            var dir = Path.Combine(skillsSrc, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
        }

        var capturedErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(capturedErr);

        try {
            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"]);
            await Assert.That(exit).IsEqualTo(1);

            // Atomicity contract: hooks.json must NOT exist after a failed install.
            var hooksPath = Path.Combine(tmp.Path, ".codex", "hooks.json");
            await Assert.That(File.Exists(hooksPath)).IsFalse();

            var stderr = capturedErr.ToString();
            await Assert.That(stderr).Contains("Cannot install Codex plugin");
        } finally {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("HOME",        originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            RemoveFakePlugin(pluginRoot);
        }
    }

    // AI-676 P2: when the kapacitor plugin folder cannot be resolved (e.g.,
    // the binary was hand-copied and the sibling `kapacitor/` tree is gone),
    // `plugin install --codex` must fail BEFORE writing hooks. Otherwise the
    // user ends up with hook entries pointing at a kapacitor binary whose
    // skills never installed, breaking the documented `--codex` contract.
    [Test, NotInParallel("CodexHookCommandTests.Console")]
    public async Task InstallCodex_fails_before_writing_hooks_when_plugin_folder_missing() {
        using var tmp = new TempDir();

        var originalHome        = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME",        tmp.Path);
        Environment.SetEnvironmentVariable("USERPROFILE", tmp.Path);

        // Defensive: make sure no leftover plant from a previous test forces
        // ResolvePluginPath to succeed. With no folder at the probed path,
        // resolver returns null and InstallCodex must short-circuit.
        var pluginRoot = ProbedPluginRoot();
        RemoveFakePlugin(pluginRoot);

        var capturedErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(capturedErr);

        try {
            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"]);
            await Assert.That(exit).IsEqualTo(1);

            // The atomic-install contract: NO hooks.json may exist in the
            // temp HOME after a failed install.
            var hooksPath = Path.Combine(tmp.Path, ".codex", "hooks.json");
            await Assert.That(File.Exists(hooksPath)).IsFalse();

            var stderr = capturedErr.ToString();
            await Assert.That(stderr).Contains("Cannot install Codex plugin");
        } finally {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("HOME",        originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
