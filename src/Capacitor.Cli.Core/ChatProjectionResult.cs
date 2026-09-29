namespace Capacitor.Cli.Core;

/// What a feed hands the chat per projected line: the rows, plus the display facts that are not
/// rows. A local slash command can acknowledge input while its wrappers stay hidden, and a
/// subagent signal can come from a row the chat hides.
public sealed record ChatProjectionResult(
        IReadOnlyList<AcpEventEnvelope> Envelopes,
        IReadOnlyList<string>           SubmittedInputs,
        IReadOnlyList<SubagentSignal>   Subagents
    );
