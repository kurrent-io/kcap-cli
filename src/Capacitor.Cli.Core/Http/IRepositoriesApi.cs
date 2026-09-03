namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/repositories/*</c> endpoints — see <see cref="ISessionsApi"/> for
/// the no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IRepositoriesApi {
    /// <summary>No distinct outcome a caller renders differently — a refusal or an unreachable server
    /// throws, and an empty result is just an empty list.</summary>
    Task<List<RepoRecapEntry>> GetRecapsAsync(string repoHash, int limit, CancellationToken ct = default);
}
