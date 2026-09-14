using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Builds the onboarding façade the discovery and login steps drive. Injected so a test can hand
/// those steps a façade over its own transport and progress sink without a process-global override.
/// </summary>
public interface IOnboardingFacadeFactory {
    OnboardingFacade Create(
        ITenantProvisioner? provisioner, ITenantPicker? picker = null, RequestedWorkspace? requested = null);
}
