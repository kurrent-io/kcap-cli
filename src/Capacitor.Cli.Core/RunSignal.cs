namespace Capacitor.Cli.Core;

/// A subagent fact a vendor's rules read off one envelope, carried beside the rows rather than on
/// the wire envelope. Vendor-neutral: which tool names spawn a subagent stays in the rules.
public abstract record RunSignal {
    /// A tool call that spawns a subagent.
    public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At) : RunSignal;

    /// The call's result was only a launch acknowledgement; the agent id is the subagent's handle
    /// from then on.
    public sealed record Detached(string CallId, string AgentId) : RunSignal;

    /// The subagent ended outside its tool result. At least one key is set. A null outcome means
    /// the source knows only that it ended.
    public sealed record Finished(string? CallId, string? AgentId, RunOutcome? Outcome, DateTimeOffset At) : RunSignal;
}
