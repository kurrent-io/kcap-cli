using kapacitor.Commands;
using static kapacitor.Commands.CodingAgentsStep;

namespace kapacitor.Tests.Unit;

public class CodingAgentsStepTests {
    [Test]
    public async Task Claude_detected_and_accepted_calls_installer_with_settings_path() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, NoPrompt: false, LegacyProjectScope: false);
        var paths    = TestPaths();
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, paths, calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsTrue();
        await Assert.That(calls.ClaudeArgs).IsEqualTo((paths.ClaudeSettingsPath, paths.PluginDir!));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code plugin installed") && l.Contains("user:"));
    }

    [Test]
    public async Task Claude_detected_and_declined_skips_installer() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: false, SkipCodex: true, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code") && l.Contains("not installed"));
    }

    [Test]
    public async Task Claude_not_detected_skips_prompt_and_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var promptCount = 0;
        var options  = new Options(SkipClaude: false, SkipCodex: true, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => { promptCount++; return true; }, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsFalse();
        await Assert.That(promptCount).IsEqualTo(0);
        await Assert.That(sink.Lines).Contains(l => l.Contains("Claude Code not found"));
    }

    [Test]
    public async Task Claude_installer_failure_emits_warning_and_returns_false() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { ClaudeReturns = false };
        var options  = new Options(SkipClaude: false, SkipCodex: true, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: true, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.ClaudeInstalled).IsFalse();
        await Assert.That(calls.ClaudeCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not update Claude settings"));
    }

    [Test]
    public async Task Codex_detected_and_accepted_installs_hooks_and_prints_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(calls.CodexHooksArg).IsEqualTo("/fake/.codex/hooks.json");
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_detected_and_declined_skips_installer_and_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => false, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_not_detected_skips_prompt_and_emits_skip_line() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: false);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex CLI not found"));
    }

    [Test]
    public async Task Codex_hooks_installer_failure_emits_warning_and_skips_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsFalse();
        await Assert.That(calls.CodexHooksCalled).IsTrue();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Could not write Codex hooks"));
        await Assert.That(sink.Lines).DoesNotContain(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    [Test]
    public async Task Codex_skills_installed_when_hooks_succeed_and_plugin_dir_present() {
        var sink     = new Sink();
        var calls    = new InstallerCalls();
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexSkillsInstalled).IsTrue();
        await Assert.That(calls.CodexSkillsArgs).IsEqualTo(("/fake/plugin/codex-skills", "/fake/.codex/skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex skills installed"));
    }

    [Test]
    public async Task Codex_skills_not_attempted_when_hooks_fail() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexHooksReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(calls.CodexSkillsCalled).IsFalse();
    }

    [Test]
    public async Task Codex_skills_failure_still_keeps_trust_hint() {
        var sink     = new Sink();
        var calls    = new InstallerCalls { CodexSkillsReturns = false };
        var options  = new Options(SkipClaude: true, SkipCodex: false, NoPrompt: false, LegacyProjectScope: false);
        var detected = new DetectedAgents(Claude: false, Codex: true);

        var result = await RunAsync(options, detected, TestPaths(), calls.AsInstallers(),
            prompt: _ => true, writeLine: sink.Write);

        await Assert.That(result.CodexHooksInstalled).IsTrue();
        await Assert.That(result.CodexSkillsInstalled).IsFalse();
        await Assert.That(sink.Lines).Contains(l => l.Contains("Codex hooks installed but skills"));
        await Assert.That(sink.Lines).Contains(l => l.Contains("/hooks") && l.Contains("trust"));
    }

    static Paths TestPaths() => new(
        ClaudeSettingsPath: "/fake/.claude/settings.json",
        PluginDir:          "/fake/plugin",
        CodexHooksPath:     "/fake/.codex/hooks.json",
        CodexSkillsDir:     "/fake/.codex/skills");

    sealed class Sink {
        public List<string> Lines { get; } = [];
        public void Write(string s) => Lines.Add(s);
    }

    sealed class InstallerCalls {
        public bool ClaudeCalled { get; private set; }
        public (string Settings, string PluginDir)? ClaudeArgs { get; private set; }
        public bool ClaudeReturns { get; set; } = true;

        public bool CodexHooksCalled { get; private set; }
        public string? CodexHooksArg  { get; private set; }
        public bool CodexHooksReturns { get; set; } = true;

        public bool CodexSkillsCalled { get; private set; }
        public (string Src, string Dst)? CodexSkillsArgs { get; private set; }
        public bool CodexSkillsReturns { get; set; } = true;

        public Installers AsInstallers() => new(
            InstallClaudePlugin: (s, p) => { ClaudeCalled = true; ClaudeArgs = (s, p); return ClaudeReturns; },
            InstallCodexHooks:   h      => { CodexHooksCalled = true; CodexHooksArg = h; return CodexHooksReturns; },
            InstallCodexSkills:  (s, d) => { CodexSkillsCalled = true; CodexSkillsArgs = (s, d); return CodexSkillsReturns; }
        );
    }
}
