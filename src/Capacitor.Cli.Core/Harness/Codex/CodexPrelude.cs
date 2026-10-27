namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its own context into the user role ahead of a turn. The server holds the same rule and both
/// pin <c>test/fixtures/codex/codex-user-lines.jsonl</c>: a turn this side declares as human must be one the
/// server counts as human, or the judge's evidence never verifies.
public static class CodexPrelude {
    static readonly string[] Markers = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>",
        "<permissions instructions>", "<recommended_plugins>",
    ];

    /// With content kinds (Codex 0.155+), a message is a person's only when every part is a <c>user.</c> kind;
    /// without any, when it does not open with a prelude marker. A prelude with text after it is not human.
    public static bool IsPrelude(string text, IReadOnlyList<string>? contentItemKinds) =>
        contentItemKinds is { Count: > 0 }
            ? contentItemKinds.Any(k => !k.StartsWith("user.", StringComparison.Ordinal))
            : Markers.Any(m => text.AsSpan().TrimStart().StartsWith(m, StringComparison.Ordinal));

    public static bool IsHuman(string text, IReadOnlyList<string>? contentItemKinds) => !IsPrelude(text, contentItemKinds);
}
