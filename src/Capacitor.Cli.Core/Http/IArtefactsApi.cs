namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/artefacts</c> — see <see cref="ISessionsApi"/> for the
/// no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IArtefactsApi {
    Task<ArtefactWriteResult> PublishAsync(PublishArtefactBody body, CancellationToken ct = default);

    Task<ArtefactWriteResult> PublishVersionAsync(string artefactId, string html, CancellationToken ct = default);

    /// <summary>Replaces the whole audience. A grant left out of <paramref name="grants"/> is one
    /// being taken away.</summary>
    Task<ArtefactWriteResult> SetVisibilityAsync(string artefactId, string visibility,
                                                 List<ArtefactGrantDto>? grants, CancellationToken ct = default);

    Task<ArtefactWriteResult> DeleteAsync(string artefactId, CancellationToken ct = default);

    Task<List<ArtefactDto>> ListAsync(CancellationToken ct = default);

    /// <summary>A page's record and content, under the same audience check as opening it in a
    /// browser. Readable by anyone the page is shared with, not only its owner.</summary>
    Task<ArtefactReadResult> ReadAsync(string artefactId, int? version, CancellationToken ct = default);

    /// <summary>Pages this profile may open whose title, description or text match
    /// <paramref name="query"/>, best first. The server clamps <paramref name="limit"/>.</summary>
    Task<ArtefactSearchResult> SearchAsync(string query, int limit, CancellationToken ct = default);
}
