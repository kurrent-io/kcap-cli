namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>One folded unified session: its finished transcript lines in history order, the
/// running token total Vibe keeps for it, and the subagents it spawned.</summary>
internal sealed record MistralVibeSession(
        IReadOnlyList<string>              Lines,
        MistralVibeTokenUsage?             Usage,
        IReadOnlyList<MistralVibeSubagent> Subagents
    );
