using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class SetupDecisionsTests {
    [Test]
    public async Task DecideInstallAgents_NoAgentsDetected_ReturnsFalseWithoutPrompting() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(
            detected, noPrompt: false, promptYesNo: _ => throw new InvalidOperationException("must not prompt"));

        await Assert.That(installAgents).IsFalse();
        await Assert.That(SetupDecisions.DetectedAgentsSummary(detected)).IsNull();
    }

    [Test]
    public async Task DecideInstallAgents_NoPromptTrue_ReturnsTrueWithoutPrompting() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(
            detected, noPrompt: true, promptYesNo: _ => throw new InvalidOperationException("must not prompt"));

        await Assert.That(installAgents).IsTrue();
    }

    [Test]
    public async Task DecideInstallAgents_Interactive_ReturnsPromptResultWhenAccepted() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt: false, promptYesNo: _ => true);

        await Assert.That(installAgents).IsTrue();
    }

    [Test]
    public async Task DecideInstallAgents_Interactive_ReturnsPromptResultWhenDeclined() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt: false, promptYesNo: _ => false);

        await Assert.That(installAgents).IsFalse();
    }

    [Test]
    public async Task DetectedAgentsSummary_KiroDetected_ContainsDefaultAgentAnnotation() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var summary = SetupDecisions.DetectedAgentsSummary(detected);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!).Contains("Kiro (installing sets kcap as your default Kiro agent)");
    }

    [Test]
    public async Task DetectedAgentsSummary_MultipleDetected_CommaJoinsFriendlyNames() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: true, Cursor: false, Copilot: false);

        var summary = SetupDecisions.DetectedAgentsSummary(detected);

        await Assert.That(summary).IsEqualTo("Claude Code, Codex");
    }
}
