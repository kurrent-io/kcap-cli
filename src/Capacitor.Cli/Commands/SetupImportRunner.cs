using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

/// <summary>The real import, aimed at the server this run chose rather than the one startup resolved.</summary>
sealed class SetupImportRunner(
        ConfigRoot config, UserHome home, HarnessRegistry harnesses, ChosenServerHttp http) : ISetupImportRunner {
    public async Task<int> RunAsync(ImportInvocation inv) {
        await using var scoped = http.For(inv.Profiles.Resolution.ServerUrl ?? "", inv.Profiles);

        return await new ImportCommand(
                config, inv.Profiles, home, harnesses, scoped.GetRequiredService<ICapacitorHttpClient>())
            .HandleImport(
            filterCwd:               null,
            filterSession:           null,
            minLines:                15,
            generateSummaries:       false,
            sources:                 SetupCommand.BuildImportSources(config, harnesses),
            explicitVendorSelection: false,
            since:                   null,
            scope:                   new ImportScope.Repo(inv.Repo.Owner, inv.Repo.Name),
            skipConfirmation:        true,
            forcePrivate:            inv.ForcePrivate,
            currentRepo:             inv.Repo,
            needOrgPick:             false,
            storedOrg:               null,
            autoSkipExclusions:      inv.AutoSkipExclusions,
            defaultVisibility:       inv.DefaultVisibility,
            nested:                  true);
    }
}
