namespace Capacitor.Cli.Commands;

/// <summary>
/// Cross-cutting filters applied to every IImportSource's discovery phase.
/// Sources that don't honour a given filter ignore the irrelevant fields.
/// </summary>
internal sealed record DiscoveryFilters(
    string?   FilterCwd,
    string?   FilterSession,
    DateOnly? Since,
    int       MinLines);

/// <summary>
/// A session candidate after DiscoverAsync. The orchestrator never inspects
/// SourceMeta; only the originating IImportSource reads it back during
/// ClassifyAsync / ImportSessionAsync.
/// </summary>
internal sealed record DiscoveredSession(
    string                                  SessionId,
    string                                  Vendor,
    string?                                 Cwd,
    DateTimeOffset?                         FirstTimestamp,
    IReadOnlyDictionary<string, object?>    SourceMeta);

/// <summary>
/// Dependencies passed to ClassifyAsync. ExcludedRepos / ExcludedPaths are
/// the user's profile-level exclusions, applied identically across sources.
/// </summary>
internal sealed record ClassifyContext(
    HttpClient                  HttpClient,
    string                      BaseUrl,
    int                         MinLines,
    IReadOnlyList<string>?      ExcludedRepos,
    IReadOnlyList<string>?      ExcludedPaths);

/// <summary>
/// Dependencies passed to ImportSessionAsync. ForcePrivate carries the
/// effective --private flag from the orchestrator (so each source can
/// stamp visibility consistently).
/// </summary>
internal sealed record ImportContext(
    HttpClient HttpClient,
    string     BaseUrl,
    bool       ForcePrivate);

internal enum ImportOutcome { Loaded, Resumed, Skipped, Failed }

/// <summary>
/// Result of <see cref="IImportSource.ImportSessionAsync"/>. <c>SentChildContent</c> is the
/// real "did new work actually happen" signal: it's true only
/// when this call POSTed brand-new nested subagent-child transcript bytes (Cursor's inline
/// child import, under a parent whose own top-level watermark may not have moved at all). The
/// parent classification/outcome pair alone can't distinguish that from a genuine "nothing to
/// do" lifecycle/repo-backfill replay — <c>ImportOutcome</c> has no line-0 signal for it, so
/// this is carried out-of-band instead of inferred. Every non-Cursor source (and Cursor itself
/// outside the nested-child path) leaves this false via the implicit <c>ImportOutcome</c>
/// conversion below.
/// </summary>
internal readonly record struct ImportSessionResult(ImportOutcome Outcome, bool SentChildContent = false) {
    public static implicit operator ImportSessionResult(ImportOutcome outcome) => new(outcome);
}

/// <summary>
/// Per-vendor import pipeline: discover candidate sessions on the local
/// machine, classify them against the server's existing state, then import
/// the ones that need work. Three implementations: ClaudeImportSource,
/// CodexImportSource, CursorImportSource.
/// </summary>
internal interface IImportSource {
    /// <summary>"claude" | "codex" | "cursor". Stamped onto every produced classification.</summary>
    string Vendor { get; }

    /// <summary>True when the source's root data dir / DB is present on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True if title-generation background tasks should be scheduled for
    /// sessions imported via this source. Claude and Codex return true;
    /// Cursor returns false because the composer header carries a name that
    /// the server maps to a SessionTitleCreatedEvent during ingest.
    /// </summary>
    bool SupportsTitleGeneration { get; }

    Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(
        DiscoveryFilters  filters,
        CancellationToken ct);

    Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
        IReadOnlyList<DiscoveredSession> sessions,
        ClassifyContext                   ctx,
        CancellationToken                 ct);

    Task<ImportSessionResult> ImportSessionAsync(
        ImportCommand.SessionClassification classification,
        ImportContext                       ctx,
        CancellationToken                   ct);
}
