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
        services.AddSingleton<ICredentialSource, ResolvingCredentialSource>();
        services.AddSingleton<ICapacitorHttpClient, CapacitorHttpClient>();
        services.AddSingleton<ISessionsApi, SessionsApi>();
        services.AddSingleton<IProjectsApi, ProjectsApi>();
        services.AddSingleton<IRepositoriesApi, RepositoriesApi>();
        services.AddSingleton<IFeedbackApi, FeedbackApi>();
        services.AddTransient<ServerVersionCaptureHandler>();
        services.AddTransient<ObservationHeaderHandler>();
        services.AddTransient<UnauthorizedRecoveryHandler>();

        services.AddHttpClient(CapacitorClients.Default)
            .AddHttpMessageHandler<ServerVersionCaptureHandler>()
            .AddHttpMessageHandler<ObservationHeaderHandler>()
            .AddHttpMessageHandler<UnauthorizedRecoveryHandler>()
            // A bearer cannot survive a redirect, so following one can only produce a misleading 401.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            // Nothing is redacted by default, so a logging provider added later would print the bearer.
            .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);

        return services;
    }
}
