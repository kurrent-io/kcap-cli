using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kcap@kcap": false } }
            """);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsClaudePlugin_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_when_kcap_command_present() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsTrue();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_no_kcap_command() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
    }

    // Fix #2: non-string command field should not throw — treated as not-installed.
    [Test]
    public async Task DetectsCodexHooks_returns_false_for_numeric_command_field() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] }
                ]
              }
            }
            """);

        // Must not throw; numeric command is not a kcap entry.
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(path)).IsFalse();
    }

    [Test]
    public async Task MuseHookGapLine_is_silent_when_every_event_is_installed() {
        await Assert.That(StatusCommand.MuseHookGapLine(MuseHooksParser.MuseHookEvents.ToHashSet())).IsNull();
    }

    [Test]
    public async Task MuseHookGapLine_names_each_missing_event() {
        var line = StatusCommand.MuseHookGapLine(new HashSet<string> { "SessionStart", "Stop" });

        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("UserPromptSubmit, SubagentStart, SubagentStop, SessionEnd");
        await Assert.That(line).DoesNotContain("SessionStart,");
    }

    [Test]
    public async Task MuseHooksDetailLine_says_an_unparseable_settings_json_is_not_valid_json() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(path, "{ \"hooks\": ");

        var line = StatusCommand.MuseHooksDetailLine(path, detected: true, wired: true);

        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("is not valid JSON");
        await Assert.That(line).DoesNotContain("hooks missing");
    }

    [Test]
    public async Task MuseHooksDetailLine_says_a_non_object_root_is_not_a_json_object() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(path, "[]");

        await Assert.That(StatusCommand.MuseHooksDetailLine(path, detected: true, wired: true)).Contains("is not a JSON object");
    }

    [Test]
    public async Task MuseHooksDetailLine_is_silent_when_every_event_is_installed() {
        using var tmp  = new TempDir();
        var       path = tmp.PathTo("settings.json");
        MuseHooksInstaller.Install(path);

        await Assert.That(StatusCommand.MuseHooksDetailLine(path, detected: true, wired: true)).IsNull();
    }

    /// <summary>An unparseable file reads as unwired, marker or not, so this is the case the line must
    /// still reach; a valid file with no hooks leaves it to the not-configured line.</summary>
    [Test]
    public async Task MuseHooksDetailLine_reports_an_unparseable_file_for_an_installed_but_unwired_muse() {
        using var tmp     = new TempDir();
        var       broken  = tmp.CreateFile("broken/settings.json", "{ \"hooks\": ");
        var       hookless = tmp.CreateFile("hookless/settings.json", "{\"schema_version\":1}");

        await Assert.That(StatusCommand.MuseHooksDetailLine(broken, detected: true, wired: false)).Contains("is not valid JSON");
        await Assert.That(StatusCommand.MuseHooksDetailLine(broken, detected: false, wired: false)).IsNull();
        await Assert.That(StatusCommand.MuseHooksDetailLine(hookless, detected: true, wired: false)).IsNull();
        await Assert.That(StatusCommand.MuseHooksDetailLine(tmp.PathTo("absent/settings.json"), detected: true, wired: false)).IsNull();
    }

    /// Codex refuses a hooks.json that starts with a byte-order mark and runs none of it, although
    /// the kcap entries parse fine here: only a wired file with the mark is refused, only Codex reads
    /// as not running, and a registry without Codex has nothing to refuse.
    [Test]
    public async Task CodexRefusedHooks_finds_a_wired_file_codex_rejects_for_its_byte_order_mark() {
        using var tmp   = new TempDir();
        var       home  = new UserHome(tmp.CreateDir("home"));
        var       clean = CodexHarness.Over(new CodexPaths(home, tmp.CreateDir("clean")));
        var       bom   = CodexHarness.Over(new CodexPaths(home, tmp.CreateDir("bom")));
        var       bare  = CodexHarness.Over(new CodexPaths(home, tmp.CreateDir("bare")));
        CodexHooksWriter.Install(clean.Paths.UserHooksJson);
        tmp.CreateFile("bom/hooks.json", "\uFEFF" + await File.ReadAllTextAsync(clean.Paths.UserHooksJson));
        tmp.CreateFile("bare/hooks.json", "\uFEFF{}");

        var refused = StatusCommand.CodexRefusedHooks(HarnessRegistry.Over(TestBinaries.None, bom));

        await Assert.That(refused).IsEqualTo(bom.Paths.UserHooksJson);
        await Assert.That(StatusCommand.CodexHooksDetailLine(refused!)).Contains("kcap plugin install --codex");
        await Assert.That(StatusCommand.CodexRefusedHooks(HarnessRegistry.Over(TestBinaries.None, clean))).IsNull();
        await Assert.That(StatusCommand.CodexRefusedHooks(HarnessRegistry.Over(TestBinaries.None, bare))).IsNull();
        await Assert.That(StatusCommand.CodexRefusedHooks(HarnessRegistry.Over(TestBinaries.None))).IsNull();
        await Assert.That(StatusCommand.HooksRun(HarnessId.Codex, wired: true, refused)).IsFalse();
        await Assert.That(StatusCommand.HooksRun(HarnessId.Codex, wired: true, null)).IsTrue();
        await Assert.That(StatusCommand.HooksRun(HarnessId.Claude, wired: true, refused)).IsTrue();
    }

    /// Every harness the build knows appears on the line — a tenth would too, since the line is
    /// built from the registry rather than a parameter per vendor.
    [Test]
    public async Task HooksStatusLine_reports_every_harness() {
        var wired = new[] { HarnessId.Claude, HarnessId.Gemini, HarnessId.Kiro, HarnessId.Pi };
        var line  = StatusCommand.BuildHooksStatusLine(
            HarnessRegistry.Identities.Select(i => (i.Id, Wired: wired.Contains(i.Id))));

        await Assert.That(line).Contains("Claude ✓");
        await Assert.That(line).Contains("Codex ✗");
        await Assert.That(line).Contains("Cursor ✗");
        await Assert.That(line).Contains("Copilot ✗");
        await Assert.That(line).Contains("Gemini ✓");
        await Assert.That(line).Contains("Kiro ✓");
        await Assert.That(line).Contains("Pi ✓");
        await Assert.That(line).Contains("OpenCode ✗");
        await Assert.That(line).Contains("Antigravity ✗");
    }

    /// Claude's label carries a product suffix everywhere else; the line has room for one word.
    [Test]
    public async Task HooksStatusLine_shortens_the_one_suffixed_label() {
        var line = StatusCommand.BuildHooksStatusLine([(HarnessId.Claude, true)]);

        await Assert.That(line).IsEqualTo("Claude ✓");
    }

    static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Configured is not recording: a wired agent that never ran a hook says what to do next,
    /// and Codex, which runs no hook until trusted, says so too.</summary>
    [Test]
    public async Task RecordingLine_tells_a_wired_but_silent_agent_what_to_do() {
        await Assert.That(StatusCommand.RecordingLine(HarnessId.Claude, "Claude Code", null, Now))
            .IsEqualTo("Claude Code: no hook event yet. Start a new Claude Code session");
        await Assert.That(StatusCommand.RecordingLine(HarnessId.Codex, "Codex", null, Now)).Contains("trust the kcap hooks");
    }

    [Test]
    [Arguments(0, "just now")]
    [Arguments(5, "5m ago")]
    [Arguments(180, "3h ago")]
    [Arguments(4320, "3d ago")]
    public async Task RecordingLine_reports_how_long_ago_the_last_hook_ran(int minutesAgo, string expected) {
        var line = StatusCommand.RecordingLine(HarnessId.Claude, "Claude Code", Now.AddMinutes(-minutesAgo), Now);

        await Assert.That(line).IsEqualTo($"Claude Code: hooks running, last hook event {expected}");
    }

    [Test]
    public async Task GitHookWarning_names_only_a_git_known_to_be_too_old() {
        await Assert.That(StatusCommand.GitHookWarning(new Version(2, 50, 1))).Contains("ignores kcap's commit hook");
        await Assert.That(StatusCommand.GitHookWarning(new Version(2, 54, 0))).IsNull();
        await Assert.That(StatusCommand.GitHookWarning(null)).IsNull();
    }

    static VendorAccount Account(HarnessId vendor, string dir) =>
        new(dir, vendor, dir, Path.GetFileName(dir), DateTimeOffset.UnixEpoch);

    static string DefaultOf(HarnessId vendor) => vendor is HarnessId.Claude ? "/h/.claude" : "/h/.codex";

    [Test]
    public async Task Account_lines_are_omitted_for_a_single_default_per_vendor() {
        var line = StatusCommand.BuildAccountLines([
            (Account(HarnessId.Claude, "/h/.claude"), RecordingState.Recording),
            (Account(HarnessId.Codex, "/h/.codex"), RecordingState.Recording),
        ], DefaultOf);

        await Assert.That(line).IsEqualTo("");
    }

    [Test]
    public async Task Account_lines_show_a_lone_account_outside_the_default_directory() {
        var line = StatusCommand.BuildAccountLines([
            (Account(HarnessId.Claude, "/h/.claude-work"), RecordingState.Recording),
        ], DefaultOf);

        await Assert.That(line).Contains(".claude-work");
    }

    [Test]
    public async Task Account_lines_list_each_account_with_its_state() {
        var line = StatusCommand.BuildAccountLines([
            (Account(HarnessId.Claude, "/h/.claude"), RecordingState.Recording),
            (Account(HarnessId.Claude, "/h/.claude-work"), RecordingState.Broken),
        ], DefaultOf);

        await Assert.That(line).Contains(".claude-work");
        await Assert.That(line).Contains("broken — run kcap accounts rewire");
        await Assert.That(line).Contains("recording");
    }

    [Test]
    public async Task Account_lines_share_the_accounts_command_wording_for_a_wired_codex_account() {
        var line = StatusCommand.BuildAccountLines([
            (Account(HarnessId.Codex, "/h/.codex"), RecordingState.Installed),
            (Account(HarnessId.Codex, "/h/.codex-work"), RecordingState.NotWired),
        ], DefaultOf);

        await Assert.That(line).Contains("hooks installed (trust in Codex)");
        await Assert.That(line).Contains("not wired");
    }
}
