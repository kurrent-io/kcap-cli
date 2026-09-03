using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Http;

internal sealed class CapacitorHttpClient(
        IHttpClientFactory factory, ICredentialSource credentials, CapacitorServer server) : ICapacitorHttpClient {
    public async Task<HttpClient> ForCommandAsync(CancellationToken ct = default) {
        // Resolved here only for the hint: the handler applies the bearer itself, since a client the
        // factory builds has no credential at build time.
        await ReportLapseAsync(await credentials.ResolveAsync(ct));

        return factory.CreateClient(CapacitorClients.Default);
    }

    async Task ReportLapseAsync(CredentialState state) {
        switch (state.Status) {
            case AuthStatus.Expired:
                await Console.Error.WriteLineAsync("Authentication token has expired. Run 'kcap login' to re-authenticate.");

                break;
            case AuthStatus.NotAuthenticated:
                // A machine cannot run `kcap login`, so telling it to is worse than saying nothing.
                await Console.Error.WriteLineAsync(
                    state.Problem is { } reason
                        ? $"Machine authentication failed: {reason}"
                        : "Not authenticated. Run 'kcap login' to authenticate.");

                break;
            case AuthStatus.WrongServer:
                await Console.Error.WriteLineAsync(
                    $"Stored token was issued by {state.Resolution?.IssuedServerUrl} but this command targets {server.Url}. " +
                    $"Run 'kcap login' (or switch profiles with 'kcap use') to authenticate against {server.Url}.");

                break;
        }
    }
}
