namespace Capacitor.Cli.Core;

/// A fact about a subagent or background command that a vendor's rules read off one envelope,
/// carried beside the rows rather than on the wire envelope. Vendor-neutral: which tool names
/// start a run stays in the rules.
public abstract record RunSignal {
    /// A tool call that may run on. A provisional start makes no row until a Detached for its
    /// call says the run went to the background.
    public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At, RunKind Kind = RunKind.Agent, bool Provisional = false) : RunSignal;

    /// The call's result was only a launch acknowledgement; the id is the run's handle from then
    /// on.
    public sealed record Detached(string CallId, string AgentId) : RunSignal;

    /// The run ended outside its tool result. At least one key is set. A null outcome means the
    /// source knows only that it ended.
    public sealed record Finished(string? CallId, string? AgentId, RunOutcome? Outcome, DateTimeOffset At) : RunSignal;
}
