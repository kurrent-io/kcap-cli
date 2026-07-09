using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
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

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsArgs).IsEqualTo((Path.Combine("/fake/plugin", "skills"), "/fake/.agents/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Agent_skills_failure_still_keeps_codex_trust_hint() {
        // Skills install and Codex hooks are independent steps — a skills-copy
        // failure must not suppress the Codex trust hint.
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
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills could not be copied"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    // ── Agent skills decoupled from Codex (AI-1285) ──────────────────────────

    [Test]
    public async Task Agent_skills_installed_when_only_cursor_detected() {
        // The bug: a Cursor-only machine (no Codex) got hooks but never the
        // shared ~/.agents/skills/ skills, because the install was gated on Codex.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.AgentSkillsArgs).IsEqualTo((Path.Combine("/fake/plugin", "skills"), "/fake/.agents/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills installed"));
    }

    [Test]
    public async Task Agent_skills_installed_when_only_copilot_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: false, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_installed_when_only_gemini_detected() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipGemini: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_still_installed_when_codex_hooks_fail() {
        // Decoupling means a Codex hooks-write failure no longer suppresses the
        // skills copy — Codex is still detected and reads ~/.agents/skills/.
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_declined_emits_hint_and_skips_install() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        // Accept Cursor hooks, decline only the skills prompt.
        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => !t.Contains("agent skills", StringComparison.OrdinalIgnoreCase),
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills not installed"));
    }

    [Test]
    public async Task Agent_skills_skipped_when_already_current() {
        // Idempotent: a marker matching this build means no prompt and no re-copy.
        var sink     = new Sink();
        var promptCount = 0;
        var calls    = new InstallerCalls { AgentSkillsCurrentReturns = true };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                if (t.Contains("agent skills", StringComparison.OrdinalIgnoreCase)) promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("already up to date"));
    }

    [Test]
    public async Task Agent_skills_not_installed_when_only_claude_detected() {
        // Claude gets skills via the bundled plugin, not ~/.agents/skills/ — a
        // Claude-only machine must not trigger the shared skills install.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
    }

    [Test]
    public async Task Agent_skills_prompted_once_across_multiple_detected_agents() {
        // A single "Install kcap agent skills?" prompt covers every skills-capable
        // agent — not one per agent.
        var sink        = new Sink();
        var promptCount = 0;
        var calls       = new InstallerCalls();
        var options     = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: false, NoPrompt: false, SkipGemini: false);
        var detected    = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: true, Gemini: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                if (t.Contains("agent skills", StringComparison.OrdinalIgnoreCase)) promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(promptCount).IsEqualTo(1);
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
    }

    [Test]
    public async Task Agent_skills_installed_without_prompt_in_no_prompt_mode() {
        var sink     = new Sink();
        var promptCount = 0;
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => {
                promptCount++;

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(result.AgentSkillsInstalled).IsTrue();
        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(promptCount).IsEqualTo(0);
    }

    [Test]
    public async Task Legacy_codex_cleanup_not_invoked_when_codex_not_detected() {
        // The ~/.codex/skills legacy sweep stays Codex-specific even though the
        // skills copy is now shared.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.AgentSkillsCalled).IsTrue();
        await Assert.That(calls.LegacyCleanupCalled).IsFalse();
    }

    [Test]
    public async Task Codex_hooks_prompt_no_longer_mentions_skills() {
        // Skills got their own standalone prompt, so the Codex hooks prompt must
        // not double-ask about skills.
        var sink     = new Sink();
        var prompts  = new List<string>();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: t => {
                prompts.Add(t);

                return true;
            },
            writeLine: sink.Write);

        await Assert.That(prompts).Contains(p => p.Contains("Install Codex CLI hooks?"));
        await Assert.That(prompts).DoesNotContain(p => p.Contains("agent skills", StringComparison.OrdinalIgnoreCase) && p.Contains("Codex"));
    }

    // ── Codex sandbox network access (AI-794) ────────────────────────────────

    [Test]
    public async Task Codex_network_access_enabled_when_hooks_installed_and_accepted() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexNetworkAccessApplied).IsTrue();
        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("network access enabled"));
    }

    [Test]
    public async Task Codex_network_access_declined_is_not_applied() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        // Accept the hooks prompt, decline only the network prompt (which is the
        // one asking to "reach your Capacitor" server).
        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: text => !text.Contains("reach your Capacitor"), writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("network access not changed"));
    }

    [Test]
    public async Task Codex_network_access_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipCodexNetworkAccess: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("--skip-codex-network-access"));
    }

    [Test]
    public async Task Codex_network_access_unchanged_when_already_enabled() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { EnableCodexNetworkReturns = CodexConfigToml.Change.Unchanged };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("already allows network access"));
    }

    [Test]
    public async Task Codex_network_access_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { EnableCodexNetworkReturns = CodexConfigToml.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(result.CodexNetworkAccessApplied).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update") && l.Contains("config.toml"));
    }

    [Test]
    public async Task Codex_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("MCP servers registered"));
    }

    [Test]
    public async Task Codex_mcp_unchanged_when_already_registered() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCodexMcpReturns = CodexConfigToml.Change.Unchanged };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("already registered"));
    }

    [Test]
    public async Task Codex_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterCodexMcpCalled).IsFalse();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Codex_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCodexMcpReturns = CodexConfigToml.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCodexMcpCalled).IsTrue();
        await Assert.That(result.CodexMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Codex MCP"));
    }

    [Test]
    public async Task Cursor_mcp_registered_when_hooks_installed() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorMcpRegistered).IsTrue();
        await Assert.That(calls.RegisterCursorMcpCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("MCP servers registered"));
    }

    [Test]
    public async Task Cursor_mcp_not_registered_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CursorHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsFalse();
        await Assert.That(calls.RegisterCursorMcpCalled).IsFalse();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
    }

    [Test]
    public async Task Cursor_mcp_skipped_by_flag() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true, SkipCursorMcp: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CursorHooksInstalled).IsTrue();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
        await Assert.That(calls.RegisterCursorMcpCalled).IsFalse();
    }

    [Test]
    public async Task Cursor_mcp_registration_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { RegisterCursorMcpReturns = JsonMcpConfigWriter.Change.Failed };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: false, SkipCopilot: true, NoPrompt: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: true, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.RegisterCursorMcpCalled).IsTrue();
        await Assert.That(result.CursorMcpRegistered).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not register Cursor MCP"));
    }

    [Test]
    public async Task Codex_network_access_not_attempted_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: false);
        var detected = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.EnableCodexNetworkCalled).IsFalse();
    }

    [Test]
    public async Task Codex_network_access_auto_applied_in_no_prompt() {
        var sink         = new Sink();
        var calls        = new InstallerCalls();
        var options      = new Options(SkipClaude: true, SkipCodex: false, SkipCursor: true, SkipCopilot: true, NoPrompt: true);
        var detected     = new DetectedAgents(Claude: false, Codex: true, Cursor: false, Copilot: false);
        var promptCalled = false;

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCalled = true; return false; }, writeLine: sink.Write);

        await Assert.That(result.CodexNetworkAccessApplied).IsTrue();
        await Assert.That(calls.EnableCodexNetworkCalled).IsTrue();
        await Assert.That(promptCalled).IsFalse();
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
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
        await Assert.That(calls.AgentSkillsCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Plugin directory not found"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Agent skills could not be installed (plugin directory not found)"));
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
        await Assert.That(result.AgentSkillsInstalled).IsFalse();
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

    [Test]
    public async Task Pi_detected_and_accepted_installs_extension() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(calls.PiExtensionArg).IsEqualTo("/fake/.pi/agent/extensions/kcap.ts");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running pi session"));
    }

    [Test]
    public async Task Pi_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();
        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension not installed"));
    }

    [Test]
    public async Task Pi_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi not detected"));
    }

    [Test]
    public async Task Pi_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Pi extension skipped by flag"));
    }

    [Test]
    public async Task Pi_no_prompt_installs_without_prompting() {
        // --no-prompt: a detected, non-skipped Pi installs without asking.
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var promptCalled = false;

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCalled = true; return false; }, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsTrue();
        await Assert.That(calls.PiExtensionCalled).IsTrue();
        await Assert.That(promptCalled).IsFalse();
    }

    [Test]
    public async Task Pi_kcap_not_on_path_aborts_install_without_writing_extension() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.PiExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Pi_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { PiExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipPi: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Pi: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.PiExtensionInstalled).IsFalse();
        await Assert.That(calls.PiExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write the Pi extension"));
    }

    // ── OpenCode (SST): a plugin file, like Pi (AI-919) ──────────────────────

    [Test]
    public async Task OpenCode_detected_and_accepted_installs_plugin() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsTrue();
        await Assert.That(calls.OpenCodeExtensionArg).IsEqualTo("/fake/.config/opencode/plugins/kcap.ts");
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("restart any running opencode session"));
    }

    [Test]
    public async Task OpenCode_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin not installed"));
    }

    [Test]
    public async Task OpenCode_not_detected_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: false);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode not detected"));
    }

    [Test]
    public async Task OpenCode_skipped_by_flag_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: true);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("OpenCode plugin skipped by flag"));
    }

    [Test]
    public async Task OpenCode_kcap_not_on_path_aborts_install_without_writing_plugin() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CapacitorOnPathReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.CapacitorOnPathCalled).IsTrue();
        await Assert.That(calls.OpenCodeExtensionCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task OpenCode_installer_failure_emits_warning() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { OpenCodeExtensionReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: false, SkipOpenCode: false);
        var detected = new DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, OpenCode: true);

        var result = await RunAsync(
            options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.OpenCodeExtensionInstalled).IsFalse();
        await Assert.That(calls.OpenCodeExtensionCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write the OpenCode plugin"));
    }

    static Paths TestPaths() => new(
        ClaudeSettingsPath:   "/fake/.claude/settings.json",
        ClaudeScopeLabel:     "user",
        PluginDir:            "/fake/plugin",
        CodexHooksPath:       "/fake/.codex/hooks.json",
        CursorHooksPath:      "/fake/.cursor/hooks.json",
        CopilotHooksPath:     "/fake/.copilot/hooks/kcap.json",
        GeminiSettingsPath:   "/fake/.gemini/settings.json",
        AgentsSkillsDir:      "/fake/.agents/skills",
        LegacyCodexSkillsDir: "/fake/.codex/skills",
        KiroHooksPath:        "/fake/.kiro/agents/kcap.json",
        PiExtensionPath:      "/fake/.pi/agent/extensions/kcap.ts",
        OpenCodeExtensionPath: "/fake/.config/opencode/plugins/kcap.ts",
        CodexConfigTomlPath:  "/fake/.codex/config.toml",
        CursorMcpPath:        "/fake/.cursor/mcp.json"
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

        public bool    GeminiHooksCalled  { get; private set; }
        public string? GeminiHooksArg     { get; private set; }
        public bool    GeminiHooksReturns { get; set; } = true;

        public bool    KiroHooksCalled  { get; private set; }
        public string? KiroHooksArg     { get; private set; }
        public bool    KiroHooksReturns { get; set; } = true;

        public bool    PiExtensionCalled  { get; private set; }
        public string? PiExtensionArg     { get; private set; }
        public bool    PiExtensionReturns { get; set; } = true;

        public bool    OpenCodeExtensionCalled  { get; private set; }
        public string? OpenCodeExtensionArg     { get; private set; }
        public bool    OpenCodeExtensionReturns { get; set; } = true;

        public bool CapacitorOnPathCalled  { get; private set; }
        public bool CapacitorOnPathReturns { get; set; } = true;

        public bool                      AgentSkillsCalled  { get; private set; }
        public (string Src, string Dst)? AgentSkillsArgs    { get; private set; }
        public bool                      AgentSkillsReturns { get; set; } = true;

        public bool    AgentSkillsCurrentCalled  { get; private set; }
        public string? AgentSkillsCurrentArg     { get; private set; }
        public bool    AgentSkillsCurrentReturns { get; set; } // default false → not current → install

        public bool    LegacyCleanupCalled  { get; private set; }
        public string? LegacyCleanupArg     { get; private set; }
        public bool    LegacyCleanupReturns { get; set; } = true;

        public bool                   EnableCodexNetworkCalled  { get; private set; }
        public CodexConfigToml.Change EnableCodexNetworkReturns { get; set; } = CodexConfigToml.Change.Updated;

        public bool                   RegisterCodexMcpCalled  { get; private set; }
        public CodexConfigToml.Change RegisterCodexMcpReturns { get; set; } = CodexConfigToml.Change.Updated;

        public bool                     RegisterCursorMcpCalled  { get; private set; }
        public JsonMcpConfigWriter.Change RegisterCursorMcpReturns { get; set; } = JsonMcpConfigWriter.Change.Updated;

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
            InstallGeminiHooks: h => {
                GeminiHooksCalled = true;
                GeminiHooksArg    = h;

                return GeminiHooksReturns;
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
            InstallPiExtension: p => {
                PiExtensionCalled = true;
                PiExtensionArg    = p;

                return PiExtensionReturns;
            },
            InstallOpenCodeExtension: p => {
                OpenCodeExtensionCalled = true;
                OpenCodeExtensionArg    = p;

                return OpenCodeExtensionReturns;
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
            },
            EnableCodexNetworkAccess: () => {
                EnableCodexNetworkCalled = true;

                return EnableCodexNetworkReturns;
            },
            RegisterCodexMcp: () => {
                RegisterCodexMcpCalled = true;

                return RegisterCodexMcpReturns;
            },
            RegisterCursorMcp: () => {
                RegisterCursorMcpCalled = true;

                return RegisterCursorMcpReturns;
            },
            AgentSkillsCurrent: dir => {
                AgentSkillsCurrentCalled = true;
                AgentSkillsCurrentArg    = dir;

                return AgentSkillsCurrentReturns;
            }
        );
    }
}
