namespace Capacitor.Cli.Core;

/// What a feed hands the chat per projected line: the rows, plus the display facts that are not
/// rows. A local slash command can acknowledge input while its wrappers stay hidden, and a
/// subagent signal can come from a row the chat hides.
public sealed record ChatProjectionResult(
        IReadOnlyList<AcpEventEnvelope> Envelopes,
        IReadOnlyList<string>           SubmittedInputs,
        IReadOnlyList<RunSignal>   Runs
    ) {
    internal IReadOnlyList<ChatProjectionResult> Parts { get; init; } = [];
    internal AcpEventEnvelope? OriginalInput { get; init; }
    public IReadOnlyList<string> LiteralInputs { get; init; } = [];

    /// A transcript cannot distinguish a typed example from some harness wrappers. Restore it only
    /// when the caller has evidence that this exact text was submitted. Resolve each envelope
    /// separately: alternatives for one receipt must never acknowledge two queued sends.
    public ChatProjectionResult RestoreSubmittedInputs(Func<string, bool> wasSubmitted, Func<string, bool>? previouslySubmitted = null) {
        if (Parts.Count > 0) return Combine(Parts.Select(part => part.RestoreSubmittedInputs(wasSubmitted, previouslySubmitted)).ToArray());
        if (OriginalInput is not { } original) return this;
        // A current send takes precedence over a historical example of the wrapper itself.
        var text = LiteralInputs.FirstOrDefault(wasSubmitted);
        if (text is null && previouslySubmitted is not null && !SubmittedInputs.Any(wasSubmitted))
            text = LiteralInputs.FirstOrDefault(previouslySubmitted);
        return text is null ? this : new([original with { Text = text }], [text], []);
    }

    internal static ChatProjectionResult Combine(IReadOnlyList<ChatProjectionResult> parts) => parts.Count == 1 ? parts[0] :
        new(parts.SelectMany(part => part.Envelopes).ToArray(),
            parts.SelectMany(part => part.SubmittedInputs).ToArray(),
            parts.SelectMany(part => part.Runs).ToArray()) {
            Parts = parts,
            LiteralInputs = parts.SelectMany(part => part.LiteralInputs).ToArray(),
        };
}
