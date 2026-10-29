namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>A child session a <c>subagent.spawn</c> effect started, named by its store directory
/// (<c>child-&lt;hex&gt;</c>, a sibling of the parent's).</summary>
internal sealed record MistralVibeSubagent(string ChildSessionId, string? AgentType) {
    const string Prefix = "child-";

    /// <summary>The id the child is recorded under: its hex suffix, since agent ids carry no hyphen.
    /// Null for a name that is not a child store.</summary>
    public string? AgentId =>
        ChildSessionId.StartsWith(Prefix, StringComparison.Ordinal)
     && ChildSessionId[Prefix.Length..] is { Length: > 0 } suffix
     && suffix.All(char.IsAsciiLetterOrDigit)
            ? suffix.ToLowerInvariant()
            : null;
}
