namespace Capacitor.Cli.Core.Policy;

public sealed record PolicyScopeDocument(PolicyScope Scope, string SourcePath, string Content, PolicyDocument Document);

public sealed record PolicySnapshot(
    string Id, IReadOnlyList<PolicyScopeDocument> Documents, bool Degraded, IReadOnlyList<string> Degradations) {
    public bool IsEmpty => Documents.Count == 0 && !Degraded;

    /// <summary>Whether a call no rule decided goes to the server's judge. The server re-reads the
    /// documents and has the final say; this only spares a round trip for a policy that never
    /// enables it.</summary>
    public bool JudgeEnabled => Documents.Any(d => d.Document.Judge?.Mode == "unmatched");
    public static readonly PolicySnapshot Empty = new("empty", [], false, []);
}
