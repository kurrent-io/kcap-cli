using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>The real import, aimed at the server this run chose rather than the one startup resolved.</summary>
sealed class SetupImportRunner(
        ConfigRoot config, UserHome home, HarnessRegistry harnesses, ChosenServerHttp http,
        GitProviderRouter router, TimeProvider time) : ISetupImportRunner {
    public async Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles) {
        ImportCommand.ImportDiscoveryResult? found = null;

        try {
            await using var scoped = http.For(profiles.Resolution.ServerUrl ?? "", profiles);

            await Command(profiles, scoped).HandleImport(
                filterCwd:    null,
                sources:      SetupCommand.BuildImportSources(config, harnesses, router, time),
                discoverOnly: true,
                onDiscovered: r => found = r,
                nested:       true);

            return new SetupImportDiscovery(found, null);
        } catch (Exception ex) {
            return new SetupImportDiscovery(null, ex);
        }
    }

    public async Task<SetupImportRun> RunAsync(ImportInvocation inv) {
        ImportRunSelection?             selection = null;
        ImportCommand.ImportRunOutcome? outcome   = null;

        try {
            await using var scoped = http.For(inv.Profiles.Resolution.ServerUrl ?? "", inv.Profiles);

            var exit = await Command(inv.Profiles, scoped).HandleImport(
                filterCwd:               null,
                filterSession:           null,
                minLines:                15,
                generateSummaries:       false,
                sources:                 SetupCommand.BuildImportSources(config, harnesses, router, time),
                explicitVendorSelection: false,
                since:                   null,
                scope:                   inv.Scope,
                skipConfirmation:        true,
                forcePrivate:            inv.ForcePrivate,
                currentRepo:             inv.CurrentRepo,
                needOrgPick:             false,
                storedOrg:               null,
                autoSkipExclusions:      inv.AutoSkipExclusions,
                defaultVisibility:       inv.DefaultVisibility,
                skipTitle:               inv.SkipTitle,
                maxSessions:             inv.MaxSessions,
                onSelected:              s => selection = s,
                onFinished:              o => outcome = o,
                nested:                  true);

            return new SetupImportRun(exit, selection, outcome, null);
        } catch (Exception ex) {
            return new SetupImportRun(1, selection, outcome, ex);
        }
    }

    ImportCommand Command(ProfileContext profiles, ServiceProvider scoped) =>
        new(config, profiles, home, harnesses, scoped.GetRequiredService<ICapacitorHttpClient>(), router, time);
}
