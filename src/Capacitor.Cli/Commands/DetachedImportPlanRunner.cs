using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Setup's detached child in plan mode: every level of an <see cref="ImportPlan"/>, uncapped and one
/// after another in this one process.
/// </summary>
/// <remarks>
/// Sequential because concurrent imports are unsafe — the OpenCode import ledger is rewritten whole
/// by each process. Each pass is aimed at the plan's server through a client built for it: the
/// container's client is bound to the profile's saved server, which on a first run is none.
/// </remarks>
sealed class DetachedImportPlanRunner(
        ConfigRoot config,
        ProfileContext profiles,
        UserHome home,
        HarnessRegistry harnesses,
        ChosenServerHttp http,
        GitProviderRouter router,
        TimeProvider time,
        Func<ImportPlanLevel, ProfileContext, Task<SetupImportRun>>? passRunner = null,
        AccountStore? accounts = null) {
    /// <returns>0 when every level finished with nothing failed; 1 otherwise.</returns>
    public async Task<int> RunAsync(string planPath) {
        if (!ImportPlan.IsSetupPlanPath(config, planPath)) {
            Console.Error.WriteLine($"Refusing the import plan at {planPath}: not a plan setup wrote in {config.Directory}.");

            return 1;
        }

        if (ImportPlan.Read(planPath) is not { } plan || !ImportPlan.IsUsableServer(plan.ServerUrl)) {
            Console.Error.WriteLine($"Could not read the import plan at {planPath}.");
            DeletePlan(planPath);

            return 1;
        }

        var context = SetupCommand.ImportContext(profiles, plan.ServerUrl);
        var scoped  = passRunner is null ? http.For(plan.ServerUrl, context) : null;
        var exit    = 0;

        try {
            foreach (var level in plan.Levels) {
                var run = passRunner is not null
                    ? await passRunner(level, context)
                    : await SetupImportLane.RunPassAsync(
                        config, context, home, scoped!.GetRequiredService<ICapacitorHttpClient>(), harnesses, router, time,
                        new SetupImportLane.Pass(level.Level, level.Repos, level.Since, level.SkipTitle, level.Vendors, MaxSessions: null),
                        accounts);

                if (run.Fault is { } fault)
                    Console.Error.WriteLine($"The {Label(level)} import failed: {fault.Message}");

                if (run.Fault is not null || run.Outcome is not { AnythingFailed: false }) exit = 1;
            }
        } finally {
            if (scoped is not null) await scoped.DisposeAsync();
            DeletePlan(planPath);
        }

        return exit;
    }

    void DeletePlan(string planPath) {
        try {
            if (ImportPlan.IsSetupPlanPath(config, planPath)) File.Delete(Path.GetFullPath(planPath));
        } catch { /* best effort; pruned with the handoff files */ }
    }

    static string Label(ImportPlanLevel level) =>
        level.Level is FirstRunImportLevel.OnlyMe ? "only-me" : "shared";
}
