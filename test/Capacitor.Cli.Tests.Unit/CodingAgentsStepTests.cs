using static Capacitor.Cli.Commands.CodingAgentsStep;

namespace Capacitor.Cli.Tests.Unit;

public class CodingAgentsStepTests {
    [Test]
    public async Task Claude_detected_and_accepted_calls_installer_with_settings_path() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var paths    = TestPaths();
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo((paths.ClaudeSettingsPath, paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code plugin installed") && l.Contains("user:"));
    }

    [Test]
    public async Task Claude_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => false,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code") && l.Contains("not installed"));
    }

    [Test]
    public async Task Claude_not_detected_skips_prompt_and_emits_skip_line() {
        var sink        = new Sink();
        var calls       = new InstallerCalls();
        var promptCount = 0;
        var options     = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected    = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => {
                promptCount++;

                return true;
            },
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code not found"));
    }

    [Test]
    public async Task Claude_installer_failure_emits_warning_and_returns_false() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { ClaudeReturns = false };
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update Claude settings"));
    }

    [Test]
    public async Task Codex_detected_and_accepted_installs_hooks_and_prints_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksArg).IsEqualTo("/fake/.codex/hooks.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_detected_and_declined_skips_installer_and_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => false,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_not_detected_skips_prompt_and_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex CLI not found"));
    }

    [Test]
    public async Task Codex_hooks_installer_failure_emits_warning_and_skips_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Codex hooks"));
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_skills_installed_when_hooks_succeed_and_plugin_dir_present() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsArgs).IsEqualTo(("/fake/plugin/skills", "/fake/.agents/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Codex_skills_not_attempted_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
    }

    [Test]
    public async Task Codex_skills_failure_still_keeps_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { AgentSkillsReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed but agent skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Plugin_dir_null_skips_claude_install_but_keeps_codex_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: true, Cursor: false, Copilot: false);
        var paths    = TestPaths() with { PluginDir = null };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Plugin directory not found"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed but agent skills could not be copied"));
    }

    [Test]
    public async Task Neither_detected_emits_warning_and_no_installer_calls() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("No supported agent CLI detected"));
    }

    [Test]
    public async Task Project_scope_claude_path_is_passed_through_to_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        // Caller (SetupCommand) selects the project-scope path AND the matching label.
        // The step's contract is to honour both faithfully.
        var paths = TestPaths() with {
            ClaudeSettingsPath = "/repo/.claude/settings.local.json",
            ClaudeScopeLabel = "project"
        };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo(("/repo/.claude/settings.local.json", paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("project:") && l.Contains("/repo/.claude/settings.local.json"));
    }

    [Test]
    public async Task Legacy_cleanup_invoked_after_successful_codex_skills_install() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(calls.LegacyCleanupCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupArg).IsEqualTo("/fake/.codex/skills");
    }

    [Test]
    public async Task Legacy_cleanup_not_invoked_when_codex_skills_install_fails() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { AgentSkillsReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options,
            detected,
            TestPaths(),
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupCalled).IsFalse();
    }

    [Test]
    public async Task Claude_path_with_markup_special_chars_does_not_break_output() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        // [ and ] are legal in paths; if not escaped, Spectre.MarkupLine throws.
        var paths = TestPaths() with {
            ClaudeSettingsPath = "/weird[path]/.claude/settings.json"
        };

        var result = await RunAsync(
            options,
            detected,
            paths,
            calls.AsInstallers(),
            prompt: _ => true,
            writeLine: sink.Write
        );

        await Assert.That(result.ClaudeInstalled).IsTrue();
        // The path must appear in the sink, escaped so Spectre treats [ and ] as literals.
        // Markup.Escape("[path]") returns "[[path]]" — Spectre escapes brackets by doubling them.
        await Assert.That(sink.Lines).Contains(l => l.Contains("[[path]]"));
    }

    [Test]
    public async Task Cursor_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsTrue();
        await Assert.That(calls.CursorHooksArg).IsEqualTo("/fake/.cursor/hooks.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks installed"));
    }

    [Test]
    public async Task Cursor_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks not installed"));
    }

    [Test]
    public async Task Cursor_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor not detected"));
    }

    [Test]
    public async Task Cursor_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Cursor hooks skipped by flag"));
    }

    [Test]
    public async Task Cursor_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CursorHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.CursorHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Cursor hooks"));
    }

    [Test]
    public async Task Cursor_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.CursorHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Copilot_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsTrue();
        await Assert.That(calls.CopilotHooksArg).IsEqualTo("/fake/.copilot/hooks/kcap.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running copilot session"));
    }

    [Test]
    public async Task Copilot_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot hooks not installed"));
    }

    [Test]
    public async Task Copilot_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot CLI not detected"));
    }

    [Test]
    public async Task Copilot_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Copilot CLI hooks skipped by flag"));
    }

    [Test]
    public async Task Copilot_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CopilotHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.CopilotHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Copilot hooks"));
    }

    [Test]
    public async Task Copilot_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CopilotHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.CopilotHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Kiro_detected_and_accepted_installs_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsTrue();
        await Assert.That(calls.KiroHooksArg).IsEqualTo("/fake/.kiro/agents/kcap.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running kiro session"));
    }

    [Test]
    public async Task Kiro_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro hooks not installed"));
    }

    [Test]
    public async Task Kiro_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Kiro CLI not detected"));
    }

    [Test]
    public async Task Kiro_kcap_not_on_path_aborts_install_without_writing_hooks() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipKiro: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.KiroHooksInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.KiroHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    static Paths TestPaths() => new(
        ClaudeSettingsPath:   "/fake/.claude/settings.json",
        ClaudeScopeLabel:     "user",
        PluginDir:            "/fake/plugin",
        CodexHooksPath:       "/fake/.codex/hooks.json",
        CursorHooksPath:      "/fake/.cursor/hooks.json",
        CopilotHooksPath:     "/fake/.copilot/hooks/kcap.json",
        AgentsSkillsDir:      "/fake/.agents/skills",
        LegacyCodexSkillsDir: "/fake/.codex/skills",
        KiroHooksPath:        "/fake/.kiro/agents/kcap.json"
    );

    sealed class Sink {
        public List<string> Lines { get; } = [];
        public void Write(string s) => Lines.Add(s);
    }

    sealed class InstallerCalls {
        public bool                                 ClaudeCalled  { get; private set; }
        public (string Settings, string PluginDir)? ClaudeArgs    { get; private set; }
        public bool                                 ClaudeReturns { get; set; } = true;

        public bool    CodexHooksCalled  { get; private set; }
        public string? CodexHooksArg     { get; private set; }
        public bool    CodexHooksReturns { get; set; } = true;

        public bool    CursorHooksCalled  { get; private set; }
        public string? CursorHooksArg     { get; private set; }
        public bool    CursorHooksReturns { get; set; } = true;

        public bool    CopilotHooksCalled  { get; private set; }
        public string? CopilotHooksArg     { get; private set; }
        public bool    CopilotHooksReturns { get; set; } = true;

        public bool    KiroHooksCalled  { get; private set; }
        public string? KiroHooksArg     { get; private set; }
        public bool    KiroHooksReturns { get; set; } = true;

        public bool CapacitorOnPathCalled  { get; private set; }
        public bool CapacitorOnPathReturns { get; set; } = true;

        public bool                      AgentSkillsCalled  { get; private set; }
        public (string Src, string Dst)? AgentSkillsArgs    { get; private set; }
        public bool                      AgentSkillsReturns { get; set; } = true;

        public bool    LegacyCleanupCalled  { get; private set; }
        public string? LegacyCleanupArg     { get; private set; }
        public bool    LegacyCleanupReturns { get; set; } = true;

        public Installers AsInstallers() => new(
            InstallClaudePlugin: (s, p) => {
                ClaudeCalled = true;
                ClaudeArgs   = (s, p);

                return ClaudeReturns;
            },
            InstallCodexHooks: h => {
                CodexHooksCalled = true;
                CodexHooksArg    = h;

                return CodexHooksReturns;
            },
            InstallCursorHooks: h => {
                CursorHooksCalled = true;
                CursorHooksArg    = h;

                return CursorHooksReturns;
            },
            InstallCopilotHooks: h => {
                CopilotHooksCalled = true;
                CopilotHooksArg    = h;

                return CopilotHooksReturns;
            },
            CapacitorOnPath: () => {
                CapacitorOnPathCalled = true;
                return CapacitorOnPathReturns;
            },
            InstallKiroHooks: h => {
                KiroHooksCalled = true;
                KiroHooksArg    = h;

                return KiroHooksReturns;
            },
            InstallAgentSkills: (s, d) => {
                AgentSkillsCalled = true;
                AgentSkillsArgs   = (s, d);

                return AgentSkillsReturns;
            },
            CleanLegacyCodexSkills: d => {
                LegacyCleanupCalled = true;
                LegacyCleanupArg    = d;

                return LegacyCleanupReturns;
            }
        );
    }
}
