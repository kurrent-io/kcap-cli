using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Core.Http;

public static class CapacitorHttpServices {
    /// <summary>
    /// Registers the authenticated client and everything it resolves. Handler order is load-bearing,
    /// but the constraint is not the order of the first two: both must sit OUTSIDE recovery. Capture
    /// so it records the response finally returned rather than a discarded 401, and the observation
    /// headers so a resend carries one copy of each rather than two.
    /// </summary>
    public static IServiceCollection AddCapacitorHttp(this IServiceCollection services) {
        services.AddCapacitorForeignClients();
        // Here rather than beside ConfigRoot: the refreshes need the anonymous and WorkOS lanes, so a
        // host that never stands up HTTP is never handed a store that can reach the network.
        services.AddSingleton<TokenStore>();
        // Singletons because each holds a cache: a per-resolve instance would re-discover and re-mint
        // on every client build, which is what these exist to avoid.
        services.AddSingleton<AuthProviderDiscovery>();
        services.AddSingleton<MachineTokenProvider>();
        services.AddSingleton<ICredentialSource, ResolvingCredentialSource>();
        services.AddSingleton<ICapacitorHttpClient, CapacitorHttpClient>();
        services.AddSingleton<ISessionsApi, SessionsApi>();
        services.AddSingleton<IProjectsApi, ProjectsApi>();
        services.AddSingleton<IRepositoriesApi, RepositoriesApi>();
        services.AddSingleton<IFeedbackApi, FeedbackApi>();
        services.AddSingleton<IMachinesApi, MachinesApi>();
        services.AddSingleton<IReviewApi, ReviewApi>();
        services.AddTransient<ServerVersionCaptureHandler>();
        // Both values are resolved once, here: the handler is registered per lane and constructed
        // per request, so reading the profile inside it would re-read config on every send.
        services.AddTransient(sp => new ObservationHeaderHandler(
            ObservationHeaderHandler.ProcessVersion,
            sp.GetRequiredService<ProfileContext>().Effective?.UpdateCheck == false));
        services.AddTransient<UnauthorizedRecoveryHandler>();

        services.AddHttpClient(CapacitorClients.Default)
            .AddHttpMessageHandler<ServerVersionCaptureHandler>()
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .AddHttpMessageHandler<UnauthorizedRecoveryHandler>()
            // A bearer cannot survive a redirect, so following one can only produce a misleading 401.
            // The lifetime matters because an MCP server holds one of these for its whole life: the
            // factory rotates handlers, but not a handler this client already has, so the pool has to
            // retire its own sockets or a DNS change never reaches a process that never restarts.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler {
                AllowAutoRedirect        = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            })
            // Nothing is redacted by default, so a logging provider added later would print the bearer.
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        // No recovery handler, because there is no credential to rotate; that is the whole point of
        // the lane rather than an omission. No version capture either: ServerVersionStore is keyed by
        // the CONFIGURED server, and these requests go to one the caller has not adopted yet, so a
        // captured version would be filed against the wrong server.
        services.AddHttpClient(CapacitorClients.Anonymous)
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            // With no bearer to strip there is nothing a redirect can break, and a download that
            // lands on an object store needs the hop followed.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true })
            // A caller may still set an Authorization header by hand on this lane.
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        services.AddLoopbackLane();

        // The observation tags ride here too, so a caller supplying its own bearer no longer has to
        // remember to attach them — which is what made the guarantee a convention rather than a rule.
        services.AddHttpClient(CapacitorClients.Bearer)
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        return services;
    }

    /// <summary>
    /// The loopback lane and nothing else, for a host with no server, no credential and no config it
    /// is allowed to read: it declares no update-check preference rather than resolving a profile to
    /// find one, which is the only part of the lane that would otherwise need config authority.
    /// </summary>
    public static IServiceCollection AddCapacitorLoopbackClient(this IServiceCollection services) {
        services.AddTransient(_ => new ObservationHeaderHandler(
            ObservationHeaderHandler.ProcessVersion, updateCheckOff: false));

        return services.AddLoopbackLane();
    }

    // No proxy: an ambient proxy setting would route a loopback grant off the machine it was minted
    // for. No redirect for the same reason — the URL is itself the credential, so a hop hands it to
    // whatever host the 3xx names. Callers set their own timeout: the lane serves a 10s manifest read
    // and a bridge that blocks until a human decides.
    static IServiceCollection AddLoopbackLane(this IServiceCollection services) {
        services.AddHttpClient(CapacitorClients.Loopback)
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .ConfigurePrimaryHttpMessageHandler(
                () => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        return services;
    }

    /// <summary>
    /// Clients that carry no credential of ours: foreign hosts, plus the sign-in exchange that mints
    /// one. None of them carries our observation headers either — a version tag we mint belongs to our
    /// own server and nowhere else. They register apart from the authenticated lanes so a process with
    /// nothing to authenticate against yet — the desktop wizard, signing up a workspace before any
    /// server exists — can take them without standing up a credential source it cannot point anywhere.
    /// </summary>
    public static IServiceCollection AddCapacitorForeignClients(this IServiceCollection services) {
        // No base address on either: their URLs come from the environment on every read, so one
        // pinned at container-build time would outlive the override it was resolved from.
        services.AddHttpClient<TenantProvisioningClient>();
        services.AddHttpClient<IAuthProxyClient, AuthProxyClient>();

        // The cap and the agent name are the registry's terms, not a caller's, so they are set here:
        // a typed client's HttpClient rejects both once it has sent its first request.
        services.AddHttpClient<NpmRegistryClient>(c => {
            c.Timeout = TimeSpan.FromSeconds(5);
            c.DefaultRequestHeaders.Add("User-Agent", "kcap-cli");
        });

        // A named lane, and WorkOSClient draws from it per call: the token store holds that client for
        // the process's life, so a typed client would freeze one handler inside it. A redirect is
        // refused outright — the refresh token and the machine secret both travel in the body, so a hop
        // would hand one to whatever host the 3xx names.
        services.AddHttpClient(CapacitorClients.WorkOS)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<WorkOSClient>();

        // github.com's device endpoints and the server-side code exchange share this: every leg carries
        // a single-use secret — a device code, or the PKCE verifier — so none of them may follow a hop.
        services.AddHttpClient(CapacitorClients.GitHub)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<GitHubOAuthClient>();

        return services;
    }
}
