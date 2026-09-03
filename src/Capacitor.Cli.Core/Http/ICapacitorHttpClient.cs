namespace Capacitor.Cli.Core.Http;

/// <summary>
/// How a caller asks for a client, expressed as intent rather than options. Each verb pairs a named
/// client with the messaging that suits its caller.
/// </summary>
public interface ICapacitorHttpClient {
    /// <summary>
    /// For an interactive command: a ready client, with the re-auth hint already on stderr when the
    /// credential could not be resolved. The caller may dispose it — that returns it to the factory
    /// without tearing down the shared handler chain.
    /// </summary>
    Task<HttpClient> ForCommandAsync(CancellationToken ct = default);
}
