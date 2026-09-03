using Capacitor.Cli.Core.Auth;
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
        services.AddSingleton<ICredentialSource, ResolvingCredentialSource>();
        services.AddSingleton<ICapacitorHttpClient, CapacitorHttpClient>();
        services.AddSingleton<ISessionsApi, SessionsApi>();
        services.AddSingleton<IProjectsApi, ProjectsApi>();
        services.AddSingleton<IRepositoriesApi, RepositoriesApi>();
        services.AddSingleton<IFeedbackApi, FeedbackApi>();
        services.AddSingleton<IMachinesApi, MachinesApi>();
        services.AddSingleton<IReviewApi, ReviewApi>();
        services.AddTransient<ServerVersionCaptureHandler>();
        services.AddTransient<ObservationHeaderHandler>();
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

        // No proxy: an ambient proxy setting would route a loopback grant off the machine it was
        // minted for. No redirect for the same reason — the URL is itself the credential, so a hop
        // hands it to whatever host the 3xx names.
        services.AddHttpClient(CapacitorClients.Loopback)
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .ConfigurePrimaryHttpMessageHandler(
                () => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        // The observation tags ride here too, so a caller supplying its own bearer no longer has to
        // remember to attach them — which is what made the guarantee a convention rather than a rule.
        services.AddHttpClient(CapacitorClients.Bearer)
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        return services;
    }

    /// <summary>
    /// Typed clients for hosts that are not our server. None of them carries our credential or our
    /// observation headers: a version tag we mint belongs to our own server and nowhere else. They
    /// register apart from the authenticated lanes so a process that talks only to a foreign host —
    /// the desktop wizard, signing up a workspace before any server exists to authenticate against —
    /// can take them without standing up a credential source it has nothing to point at.
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

        return services;
    }
}
