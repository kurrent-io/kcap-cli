using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Picks the credential source this process needs, on first use. Discovery is async and a container
/// cannot construct asynchronously, so the choice happens inside the call and is memoized for the
/// instance's life.
/// </summary>
internal sealed class ResolvingCredentialSource(CapacitorServer server) : ICredentialSource {
    ICredentialSource? _chosen;

    public async Task<CredentialState> ResolveAsync(CancellationToken ct) =>
        await (await ChooseAsync(ct)).ResolveAsync(ct);

    public async Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) =>
        await (await ChooseAsync(ct)).RotateAsync(refused, ct);

    async Task<ICredentialSource> ChooseAsync(CancellationToken ct) {
        if (Volatile.Read(ref _chosen) is { } already) return already;

        var picked = await PickAsync(ct);

        // Two concurrent first calls may both pick. The sources hold no state of their own — the
        // caches they read are elsewhere — so the loser's instance is simply dropped.
        return Interlocked.CompareExchange(ref _chosen, picked, null) ?? picked;
    }

    async Task<ICredentialSource> PickAsync(CancellationToken ct) {
        if (await HttpClientExtensions.DiscoverProviderAsync(server.Url, server.Config, server.Profiles, ct) == "None")
            return NoCredentials.Instance;

        // Before the token store: a runner has no profile, so that path would find nothing and advise
        // `kcap login`, which a runner cannot do. Gated on Intended, not on both variables, so a
        // half-configured one is told which is missing.
        if (MachineAuth.Intended)
            return MachineAuth.TryRead(out var problem) is { } credential
                ? new MachineCredentials(credential)
                : new Unusable(problem!);

        return new TokenStoreCredentials(server.Config, server.Profiles.Name, server.Url);
    }

    sealed class Unusable(string problem) : ICredentialSource {
        public Task<CredentialState> ResolveAsync(CancellationToken ct) =>
            Task.FromResult(new CredentialState(null, AuthStatus.NotAuthenticated, Problem: problem));

        public Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) => ResolveAsync(ct);
    }
}
