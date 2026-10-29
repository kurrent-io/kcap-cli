using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Runs the Import step's embedded <c>kcap import</c>. Neither call throws: setup reads the
/// fault out of the result, so the step can never abort the wizard.</summary>
internal interface ISetupImportRunner {
    Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles);
    Task<SetupImportRun>       RunAsync(ImportInvocation invocation);
}
