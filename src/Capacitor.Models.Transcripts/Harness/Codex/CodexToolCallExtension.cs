namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex` on a namespaced tool call: the split the canonical `{namespace}{name}` joins.
public sealed record CodexToolCallExtension {
    public string? Name      { get; init; }
    public string? Namespace { get; init; }
}
