namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The bound scope: the whole manifest, the current token and the client-clock deadline it stays valid until.</summary>
public sealed record EvidenceScopeState(string ScopeVersion, string RootSessionId, bool Complete, IReadOnlyList<string> IncompleteReasons,
    IReadOnlyList<EvidenceSourceDto> Sources, string Token, DateTimeOffset Deadline, DateTimeOffset? ServerExpiresAt);
