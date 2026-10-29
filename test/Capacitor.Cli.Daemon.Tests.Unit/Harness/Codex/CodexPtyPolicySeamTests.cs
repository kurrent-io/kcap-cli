using System.Text.Json;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>What the terminal-hosted Codex lane tells the judge about a refusal.</summary>
public class CodexPtyPolicySeamTests {
    const string Session = "s-1";

    /// Codex's permission prompt names no tool id, yet a refusal there must still be declared, not leave the
    /// session's refusals incomplete for the rest of its run.
    [Test]
    public async Task A_refusal_without_a_tool_id_is_declared() {
        var ledger = new PermissionRefusalLedger();
        ledger.MarkBridgedSinceStart(Session);
        using var input = JsonDocument.Parse("""{"command":"git push"}""");

        CodexPtyPolicySeam.RecordRefusal(ledger,
            new ClaudeHostedPermissionCall(Session, "agent-1", "Bash", input.RootElement.Clone(), "/wt", ToolUseId: null, TranscriptPath: null));

        var declared = ledger.Declare(Session);
        await Assert.That(declared.Complete).IsTrue();
        await Assert.That(declared.Entries.Single().Target).IsEqualTo("git push");
    }
}
