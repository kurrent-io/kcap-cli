using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffVendorEligibilityTests {
    static CodingAgentsStep.Paths Paths(TempDir root) => new(
        ClaudeSettingsPath: root.PathTo("claude-settings.json"), ClaudeScopeLabel: "user", PluginDir: root.PathTo("plugin"),
        CodexHooksPath: root.PathTo("codex-hooks"), CursorHooksPath: root.PathTo("cursor-hooks"), CopilotHooksPath: root.PathTo("copilot-hooks"),
        GeminiSettingsPath: root.PathTo("gemini-settings.json"), AgentsSkillsDir: root.PathTo("agents-skills"), LegacyCodexSkillsDir: root.PathTo("legacy"),
        KiroSkillsDir: root.PathTo("kiro-skills"), AntigravitySkillsDir: root.PathTo("antigravity-skills"));

    static void InstallShared(TempDir root)      => root.CreateFile(["agents-skills", "kcap-eval-watch", "SKILL.md"], "skill");
    static void InstallKiro(TempDir root)        => root.CreateFile(["kiro-skills", "kcap-eval-watch", "SKILL.md"], "skill");

    [Test]
    public async Task A_detected_vendor_with_the_skill_is_eligible_and_launchable_only_when_its_binary_resolves() {
        using var root = new TempDir();
        InstallShared(root);
        var harnesses = TestHarnesses.All(detected: [HarnessId.Codex, HarnessId.Pi]);   // TestBinaries.None: nothing resolves

        var eligible = HandoffVendorEligibility.Eligible(harnesses, Paths(root));

        await Assert.That(eligible.Select(v => v.Id).ToList()).IsEquivalentTo([HarnessId.Codex, HarnessId.Pi]);
        await Assert.That(eligible.All(v => !v.Launchable)).IsTrue();
        await Assert.That(HandoffVendorEligibility.Detected(harnesses)).IsEqualTo(2);
    }

    [Test]
    public async Task Kiro_reads_its_own_skills_directory_not_the_shared_tree() {
        using var root = new TempDir();
        InstallShared(root);
        var harnesses = TestHarnesses.All(detected: [HarnessId.Kiro]);

        await Assert.That(HandoffVendorEligibility.Eligible(harnesses, Paths(root))).IsEmpty();

        InstallKiro(root);
        await Assert.That(HandoffVendorEligibility.Eligible(harnesses, Paths(root)).Select(v => v.Id).ToList()).IsEquivalentTo([HarnessId.Kiro]);
    }

    [Test]
    public async Task An_undetected_vendor_is_never_eligible_even_with_the_skill_installed() {
        using var root = new TempDir();
        InstallShared(root);

        await Assert.That(HandoffVendorEligibility.Eligible(TestHarnesses.All(detected: []), Paths(root))).IsEmpty();
    }
}
