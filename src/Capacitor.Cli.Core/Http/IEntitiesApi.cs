namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/work-items/entities*</c> endpoints — see <see cref="ISessionsApi"/>
/// for the no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows. The registry is deployment configuration, so it has to be writable from where the names
/// actually live rather than only from the browser.</summary>
public interface IEntitiesApi {
    Task<EntityRegistryResult> GetAsync(string repoHash, CancellationToken ct = default);

    Task<EntityRegistryResult> RegisterAsync(string repoHash, string value, CancellationToken ct = default);

    Task<EntityRegistryResult> WithdrawAsync(string repoHash, string value, CancellationToken ct = default);
}
