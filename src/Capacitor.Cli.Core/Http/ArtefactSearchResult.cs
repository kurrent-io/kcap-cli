namespace Capacitor.Cli.Core.Http;

public abstract record ArtefactSearchResult {
    /// <summary>The pages this profile may open that match, best first. Possibly none.</summary>
    public sealed record Found(IReadOnlyList<PageSearchHitDto> Pages) : ArtefactSearchResult;

    /// <summary>The server predates page search.</summary>
    public sealed record ServerTooOld : ArtefactSearchResult;
}
