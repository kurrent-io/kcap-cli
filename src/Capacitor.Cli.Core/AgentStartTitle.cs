namespace Capacitor.Cli.Core;

/// <summary>
/// The title a hosted agent's session starts with. An explicit title is the caller's and the
/// server keeps it; a <see cref="Derived"/> one is a placeholder cut from the prompt, which a
/// generated title replaces.
/// </summary>
public readonly record struct AgentStartTitle(string Text, bool Derived) {
    public const int MaxDerivedLength = 80;

    public static AgentStartTitle? FromPrompt(string? prompt) =>
        Shorten(prompt) is { } text ? new AgentStartTitle(text, Derived: true) : null;

    /// <summary>
    /// The start title of <c>kcap agent start</c>: <paramref name="explicitTitle"/> when given,
    /// else the last passthrough argument taken as the prompt. That argument counts only when
    /// neither it nor the token before it starts with <c>-</c>, so a flag's value is never
    /// mistaken for a prompt.
    /// </summary>
    public static AgentStartTitle? ForLocalStart(string? explicitTitle, IReadOnlyList<string> passthrough) {
        if (!string.IsNullOrWhiteSpace(explicitTitle)) return new AgentStartTitle(explicitTitle.Trim(), Derived: false);

        if (passthrough.Count == 0) return null;

        var last = passthrough[^1];
        if (last.StartsWith('-')) return null;
        if (passthrough.Count > 1 && passthrough[^2].StartsWith('-')) return null;

        return FromPrompt(last);
    }

    /// <summary>First non-blank line of <paramref name="prompt"/>, trimmed, capped at
    /// <see cref="MaxDerivedLength"/> characters including the ellipsis that marks a cut, never
    /// splitting a surrogate pair.</summary>
    public static string? Shorten(string? prompt) {
        if (prompt is null) return null;

        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length <= MaxDerivedLength) return line;

            var cut = char.IsHighSurrogate(line[MaxDerivedLength - 2]) ? MaxDerivedLength - 2 : MaxDerivedLength - 1;

            return line[..cut] + "…";
        }

        return null;
    }
}
