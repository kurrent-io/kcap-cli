using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Hands the discovery and login steps a façade the test built, over its own transport and progress
/// sink. The picker and the requested workspace are the caller's to pass and are not substituted here.
/// </summary>
sealed class FakeFacadeFactory(Func<ITenantProvisioner?, OnboardingFacade> build) : IOnboardingFacadeFactory {
    public OnboardingFacade Create(
            ITenantProvisioner? provisioner, ITenantPicker? picker = null, RequestedWorkspace? requested = null) =>
        build(provisioner);
}
