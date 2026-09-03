using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Http;

internal sealed class CapacitorHttpClient(
        IHttpClientFactory factory, ICredentialSource credentials, CapacitorServer server) : ICapacitorHttpClient {
    public async Task<HttpClient> ForCommandAsync(CancellationToken ct = default) {
        // The only verb that throws, and it is not in a send path: a command with a user present owes
        // them the hint and an exit code, which the other verbs' callers have no way to render.
        if (!server.Usable) throw new UnusableServerUrlException(HttpClientExtensions.SchemeMissingHint);

        // Resolved here only for the hint: the handler applies the bearer itself, since a client the
        // factory builds has no credential at build time.
        await ReportLapseAsync(await credentials.ResolveAsync(ct));

        return factory.CreateClient(CapacitorClients.Default);
    }

    public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) =>
        Task.FromResult(factory.CreateClient(CapacitorClients.Default));

    public async Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) {
        // Answered before anything is spent: an unusable URL reaches no token store, no discovery and
        // no socket, and the caller's not-usable branch already knows what to do with it.
        if (!server.Usable)
            return new AuthAttempt(
                factory.CreateClient(CapacitorClients.Default),
                AuthStatus.UnusableServerUrl, HttpClientExtensions.SchemeMissingHint);

        var state = await credentials.ResolveAsync(ct);

        return new AuthAttempt(
            factory.CreateClient(CapacitorClients.Default),
            state.Status, state.Problem, state.Resolution?.IssuedServerUrl);
    }

    // Resolves nothing: the hint is the only reason ForCommandAsync does, and the handler applies
    // the bearer on send either way.
    public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) =>
        Task.FromResult(factory.CreateClient(CapacitorClients.Default));

    public HttpClient Anonymous() => factory.CreateClient(CapacitorClients.Anonymous);

    public HttpClient Loopback() => factory.CreateClient(CapacitorClients.Loopback);

    public HttpClient Bearer() => factory.CreateClient(CapacitorClients.Bearer);

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
