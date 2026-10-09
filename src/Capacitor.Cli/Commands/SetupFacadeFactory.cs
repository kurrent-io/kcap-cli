using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>The façade the real run drives: console progress on the step margin, and the browser-or-
/// terminal workspace pick.</summary>
sealed class SetupFacadeFactory(
        ConfigRoot config, TokenStore store, IHttpClientFactory httpFactory, IAuthProxyClient proxy,
        GitHubOAuthClient github, WorkOSClient workos, IBrowserLauncher browser,
        CliTelemetry telemetry, AuthEndpoints endpoints, TimeProvider time,
        TenantProvisioningClient provisioning) : IOnboardingFacadeFactory {
    public OnboardingFacade Create(
            ITenantProvisioner? provisioner, ITenantPicker? picker = null, RequestedWorkspace? requested = null,
            IAuthProgress? progress = null) =>
        new OnboardingFacade(config, store, httpFactory, proxy, github, workos, progress ?? SetupCommand.StepProgress, browser,
            picker ?? SetupCommand.DefaultPicker(browser, () => true, time), provisioner, telemetry, endpoints,
            time, SetupCommand.WorkspaceGuard(requested)) {
            KeyWatcher     = ConsoleKeyWatcher.Instance,
            ProbeWorkspace = ProbeWorkspaceAsync
        };

    /// <summary>
    /// A workspace still being created 404s at its address too, and must not be read as removed: the
    /// create offer it would lead to cannot make a second one while the first is pending.
    /// </summary>
    async Task<WorkspaceAnswer> ProbeWorkspaceAsync(DiscoveredTenant tenant, string bearer, CancellationToken ct) {
        WorkspaceAnswer answer;

        using (var client = httpFactory.CreateClient(CapacitorClients.Anonymous))
            answer = await WorkspaceProbe.AskAsync(client, tenant.Origin, time, ct);

        if (answer != WorkspaceAnswer.Gone || tenant.Slug is not { Length: > 0 } slug) return answer;

        var status = await provisioning.GetStatusAsync(endpoints.SignupUrl, bearer, slug, ct);

        return ProvisioningPoll.IsPending(status.StatusCode, status.Body?.State) ? WorkspaceAnswer.NoAnswer : answer;
    }
}
