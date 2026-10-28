namespace Capacitor.Cli.Core.Http;

public abstract record ArtefactReadResult {
    /// <summary>The page's record and the HTML of <paramref name="Version"/>, which is the version
    /// asked for or, when none was, the latest one the record names.</summary>
    public sealed record Read(ArtefactDto Artefact, int Version, string Html) : ArtefactReadResult;

    /// <summary>No such page or version, or none this profile may open — the server does not
    /// distinguish them, and neither does this.</summary>
    public sealed record NotFound : ArtefactReadResult;
}
