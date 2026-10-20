namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex` on a SessionStarted, from the rollout's `session_meta`. Every property is
/// written, null or not, because the server's packing writes them all.
public sealed record CodexSessionExtension {
    public string?   Cwd           { get; init; }
    public string?   Originator    { get; init; }
    public string?   CliVersion    { get; init; }
    public string?   Source        { get; init; }
    public string?   ModelProvider { get; init; }
    public CodexGit? Git           { get; init; }
    public string?   WorkspaceRoot { get; init; }
}
