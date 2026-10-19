using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli;

/// <summary>
/// A daemon stopping an agent for resume marks its ledger entry <c>Resuming</c> first; the hook and
/// the watcher consult it so the stop does not end the session the resume continues.
/// </summary>
static class ResumeEndSuppression {
    public static bool ShouldSuppress(HostedAgent hosted, Func<string, ResumeLedgerEntry?> lookup) {
        if (hosted.AgentId is not { } agentId) return false;

        // A broken ledger must never keep a real session end from posting.
        try {
            return lookup(agentId) is { State: ResumeState.Resuming };
        } catch {
            return false;
        }
    }

    public static bool ShouldSuppress(HostedAgent hosted) =>
        ShouldSuppress(hosted, id => ResumeLedgerStore.FindByAgentId(DaemonStore.FromEnvironment(), id));
}
