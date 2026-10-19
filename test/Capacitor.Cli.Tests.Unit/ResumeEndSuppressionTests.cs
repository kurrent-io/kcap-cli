using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Tests.Unit;

public class ResumeEndSuppressionTests {
    static HostedAgent Agent(string? id) => new(id, false, DaemonBridge.None);

    static ResumeLedgerEntry Entry(ResumeState state) => ResumeLedgerEntries.For("agent-1", state);

    [Test]
    public async Task Suppresses_only_a_resuming_entry() {
        await Assert.That(ResumeEndSuppression.ShouldSuppress(Agent("agent-1"), _ => Entry(ResumeState.Running))).IsFalse();
        await Assert.That(ResumeEndSuppression.ShouldSuppress(Agent("agent-1"), _ => Entry(ResumeState.Resuming))).IsTrue();
        await Assert.That(ResumeEndSuppression.ShouldSuppress(Agent("agent-1"), _ => null)).IsFalse();
        await Assert.That(ResumeEndSuppression.ShouldSuppress(Agent(null), _ => Entry(ResumeState.Resuming))).IsFalse();
    }

    [Test]
    public async Task An_unreadable_ledger_fails_open() {
        await Assert.That(ResumeEndSuppression.ShouldSuppress(Agent("agent-1"), _ => throw new IOException("broken"))).IsFalse();
    }
}
