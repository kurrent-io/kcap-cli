using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>The façade the real run drives: console progress on the step margin, and the browser-or-
/// terminal workspace pick.</summary>
sealed class SetupFacadeFactory(
        ConfigRoot config, TokenStore store, IHttpClientFactory httpFactory, IAuthProxyClient proxy,
        GitHubOAuthClient github, WorkOSClient workos, IBrowserLauncher browser,
        CliTelemetry telemetry, AuthEndpoints endpoints, TimeProvider time) : IOnboardingFacadeFactory {
    public OnboardingFacade Create(
            ITenantProvisioner? provisioner, ITenantPicker? picker = null, RequestedWorkspace? requested = null,
            IAuthProgress? progress = null) =>
        new OnboardingFacade(config, store, httpFactory, proxy, github, workos, progress ?? SetupCommand.StepProgress, browser,
            picker ?? SetupCommand.DefaultPicker(browser, () => true, time), provisioner, telemetry, endpoints,
            time, SetupCommand.WorkspaceGuard(requested)) {
            KeyWatcher = ConsoleKeyWatcher.Instance
        };
}
