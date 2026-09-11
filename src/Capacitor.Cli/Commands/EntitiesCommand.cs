using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap entities</c> — the repo's registry of things it deploys, and the names still waiting on
/// an answer. The names themselves live in the deployment repo's values, so declaring them has to be
/// scriptable from there rather than only clickable in the dashboard.
///
/// <para>Every route 403s with <c>work_items_not_in_plan</c> on the Free plan.</para>
/// </summary>
class EntitiesCommand(ConfigRoot config, IEntitiesApi entities) {
    public async Task<int> HandleAsync(string[] args) {
        var (verb, rest) = args.Length > 1 ? (args[1], args[2..]) : ("list", []);

        return verb switch {
            "list"                     => await RunAsync(hash => entities.GetAsync(hash)),
            "add" or "register" when rest.Length == 2
                                       => await RunAsync(hash => entities.RegisterAsync(hash, rest[0], rest[1])),
            "remove" or "withdraw" when rest.Length == 1
                                       => await RunAsync(hash => entities.WithdrawAsync(hash, rest[0])),
            _                          => Usage(),
        };
    }

    async Task<int> RunAsync(Func<string, Task<EntityRegistryResult>> call) {
        var repo = await RepositoryDetection.DetectRepositoryAsync(config, Environment.CurrentDirectory);

        if (repo?.Owner is null || repo.RepoName is null) {
            await Console.Error.WriteLineAsync("Could not determine the repo's owner/name from its git remote.");

            return 1;
        }

        EntityRegistryResult result;

        try {
            result = await call(RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName));
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        switch (result) {
            case EntityRegistryResult.Forbidden(var code):
                await Console.Error.WriteLineAsync(code == "work_items_not_in_plan"
                    ? "Work Items require the Team or Enterprise plan."
                    : "Not permitted.");

                return 1;

            case EntityRegistryResult.Rejected(var detail):
                await Console.Error.WriteLineAsync(detail);

                return 1;

            case EntityRegistryResult.NotFound:
                await Console.Error.WriteLineAsync(
                    "Nothing to change, or this repo is not visible for this profile. Check `kcap whoami` / your active profile.");

                return 1;

            default:
                Render(((EntityRegistryResult.Found)result).Registry);

                return 0;
        }
    }

    static void Render(CliEntityRegistry registry) {
        if (registry.Registered.Count == 0) {
            AnsiConsole.WriteLine("Nothing registered yet.");
        } else {
            var table = new Table().Border(TableBorder.Rounded).Title("Registered");
            table.AddColumn("Name");
            table.AddColumn("Kind");
            table.AddColumn("Source");

            foreach (var row in registry.Registered)
                table.AddRow(Markup.Escape(row.Value), Markup.Escape(row.Kind), Markup.Escape(row.Source));

            AnsiConsole.Write(table);
        }

        if (!registry.Curated) {
            AnsiConsole.WriteLine(
                "Nothing declared yet, so every proposed name still groups sessions. "
              + "Declaring the first name makes the registry the decision for this repo.");

            return;
        }

        if (registry.Candidates.Count == 0) return;

        var waiting = new Table().Border(TableBorder.Rounded).Title("Waiting for an answer");
        waiting.AddColumn("Name");
        waiting.AddColumn("Sessions");

        foreach (var row in registry.Candidates)
            waiting.AddRow(Markup.Escape(row.Value), row.Sessions.ToString());

        AnsiConsole.Write(waiting);
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap entities [list]");
        Console.Error.WriteLine("       kcap entities add <name> <tenant|service|environment|resource|config|team>");
        Console.Error.WriteLine("       kcap entities remove <name>");

        return 1;
    }
}
