namespace Capacitor.Models.Transcripts.Harness.Codex;

public sealed record CodexGit {
    public string? CommitHash    { get; init; }
    public string? Branch        { get; init; }
    public string? RepositoryUrl { get; init; }
}
