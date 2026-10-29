namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex` on assistant text Codex tagged with a layout phase (`commentary`, `final`).
public sealed record CodexAssistantTextExtension {
    public string? Phase { get; init; }
}
