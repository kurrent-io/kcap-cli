using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap local</c>: runs and removes the local installation that <c>kcap setup --local</c> made. A
/// subcommand reaches the installation through <see cref="LocalEdition.ILocalStackFactory"/>.</summary>
static class LocalCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var sub = args.Length > 1 ? args[1] : null;

        return sub switch {
            null or "help" => await PrintUsage(sub is null ? 1 : 0),
            _              => await Unknown(sub),
        };
    }

    static async Task<int> Unknown(string sub) {
        await Console.Error.WriteLineAsync($"Unknown subcommand: kcap local {sub}");
        return await PrintUsage(1);
    }

    static async Task<int> PrintUsage(int exit) {
        var usage = EmbeddedResources.TryLoad("help-local.txt") ?? "Usage: kcap local <subcommand>";

        if (exit == 0) await Console.Out.WriteAsync(usage);
        else await Console.Error.WriteAsync(usage);

        return exit;
    }
}
