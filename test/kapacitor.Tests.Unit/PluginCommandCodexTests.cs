using System.Text.Json.Nodes;
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

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

            var inner = entries[0]!["hooks"]!.AsArray();
            await Assert.That(inner[0]!["type"]!.GetValue<string>()).IsEqualTo("command");
            await Assert.That(inner[0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor codex-hook");
            await Assert.That(inner[0]!["timeout"]!.GetValue<int>()).IsEqualTo(30);
        }
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
        await Assert.That(PluginCommand.EntryReferencesKapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_true_for_matching_string_command() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}""");
        await Assert.That(PluginCommand.EntryReferencesKapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_for_null() {
        await Assert.That(PluginCommand.EntryReferencesKapacitorCodexHook(null)).IsFalse();
    }

    // ---- Codex skill install / remove ----

    [Test]
    public async Task InstallCodexSkills_copies_known_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "codex-skills");
        var       target = Path.Combine(tmp.Path, "skills");
        WriteSkill(source, "kapacitor-recap",  "recap body");
        WriteSkill(source, "kapacitor-errors", "errors body");

        var ok = PluginCommand.InstallCodexSkills(source, target);
        await Assert.That(ok).IsTrue();

        var recap  = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap",  "SKILL.md"));
        var errors = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-errors", "SKILL.md"));
        await Assert.That(recap).IsEqualTo("recap body");
        await Assert.That(errors).IsEqualTo("errors body");
    }

    [Test]
    public async Task InstallCodexSkills_overwrites_existing_kapacitor_skill() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "codex-skills");
        var       target = Path.Combine(tmp.Path, "skills");
        WriteSkill(source, "kapacitor-recap",  "new recap");
        WriteSkill(source, "kapacitor-errors", "new errors");

        // Pre-existing stale copy in target.
        WriteSkill(target, "kapacitor-recap", "stale recap");

        PluginCommand.InstallCodexSkills(source, target);

        var recap = await File.ReadAllTextAsync(Path.Combine(target, "kapacitor-recap", "SKILL.md"));
        await Assert.That(recap).IsEqualTo("new recap");
    }

    [Test]
    public async Task InstallCodexSkills_preserves_foreign_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "codex-skills");
        var       target = Path.Combine(tmp.Path, "skills");
        WriteSkill(source, "kapacitor-recap",  "recap");
        WriteSkill(source, "kapacitor-errors", "errors");

        // Foreign skill the user installed themselves — must not be touched.
        WriteSkill(target, "user-skill", "user content");

        PluginCommand.InstallCodexSkills(source, target);

        var foreign = await File.ReadAllTextAsync(Path.Combine(target, "user-skill", "SKILL.md"));
        await Assert.That(foreign).IsEqualTo("user content");
    }

    [Test]
    public async Task InstallCodexSkills_returns_false_for_missing_source() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "nonexistent");
        var       target = Path.Combine(tmp.Path, "skills");

        var ok = PluginCommand.InstallCodexSkills(source, target);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task RemoveCodexSkills_deletes_known_skills_only() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "skills");
        WriteSkill(target, "kapacitor-recap",  "recap");
        WriteSkill(target, "kapacitor-errors", "errors");
        WriteSkill(target, "user-skill",       "user content");

        var ok = PluginCommand.RemoveCodexSkills(target);
        await Assert.That(ok).IsTrue();

        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-recap"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kapacitor-errors"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, "user-skill", "SKILL.md"))).IsTrue();
    }

    [Test]
    public async Task RemoveCodexSkills_returns_false_when_nothing_to_remove() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "skills");
        Directory.CreateDirectory(target);

        var ok = PluginCommand.RemoveCodexSkills(target);
        await Assert.That(ok).IsFalse();
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
