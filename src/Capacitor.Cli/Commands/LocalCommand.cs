using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>kcap local</c> group, for the installation <c>kcap setup --local</c> made. A subcommand reaches it
/// through <see cref="ILocalStackFactory"/>.</summary>
sealed class LocalCommand(ILocalStackFactory stacks, IOutgoingRecorders recorders, ConfigRoot config, UserHome home) {
    static readonly string[] UninstallFlags = ["--purge", "--force", "--yes", "-y"];

    public async Task<int> HandleAsync(string[] args) {
        var sub = args.Length > 1 ? args[1] : null;

        return sub switch {
            null or "help" => await PrintUsage(sub is null ? 1 : 0),
            "uninstall"    => await UninstallAsync(args[2..]),
            _              => await Unknown(sub),
        };
    }

    async Task<int> UninstallAsync(string[] args) {
        if (args.FirstOrDefault(a => !UninstallFlags.Contains(a)) is { } unknown) {
            await Console.Error.WriteLineAsync($"Unknown option for kcap local uninstall: {unknown}");
            return await PrintUsage(1);
        }

        var purge = args.Contains("--purge");
        var force = args.Contains("--force");
        var yes   = args.Contains("--yes") || args.Contains("-y");

        LocalStack stack;

        try {
            stack = stacks.Create();
        } catch (LocalInstallFailure ex) {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        var root = stack.Root.Directory;

        switch (stack.Read()) {
            case LocalStackState.NotInstalled:
                await Console.Out.WriteLineAsync($"There is no local installation in {root}.");
                return 0;

            case LocalStackState.Unreadable(var path, var reason):
                await Console.Error.WriteLineAsync($"Error: {path} cannot be read ({reason}). Nothing was changed.");
                return 1;

            case LocalStackState.Interrupted(var operation, _):
                await Console.Out.WriteLineAsync($"An interrupted {operation} of the local installation is finished first.");
                break;

            case LocalStackState.Installed installed:
                await DescribeAsync(installed.Installation, root, purge);
                break;
        }

        if (!yes) {
            if (Console.IsInputRedirected) {
                await Console.Error.WriteLineAsync("Error: pass --yes to uninstall without a prompt.");
                return 1;
            }

            await Console.Out.WriteAsync("Proceed? [y/N] ");

            if (!string.Equals((await Console.In.ReadLineAsync())?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");
                return 0;
            }
        }

        var progress = new SyncProgress<LocalInstallEvent>(Render);
        var uninstall = new LocalUninstall(stack.Service, home, recorders);

        LocalUninstallOutcome outcome;

        try {
            outcome = await stack.RunAsync(progress,
                (lease, state, ct) => uninstall.RunAsync(lease, state, purge, force, config.Directory, ct), CancellationToken.None, force);
        } catch (Exception ex) when (ex is LocalLifecycleException or LocalInstallFailure) {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        await Console.Out.WriteLineAsync(outcome switch {
            LocalUninstallOutcome.Purged      => $"Purged the local installation in {root}.",
            LocalUninstallOutcome.Uninstalled => $"Uninstalled the local server. Its data in {root} is kept; kcap setup --local brings it back.",
            _                                 => $"There is no local installation in {root}.",
        });

        return 0;
    }

    async Task DescribeAsync(LocalInstallation installation, string root, bool purge) {
        await Console.Out.WriteLineAsync($"This will uninstall the local server at {installation.ServerUrl}:");
        await Console.Out.WriteLineAsync("  • Stop every kcap daemon and watcher, and remove the daemon service");
        await Console.Out.WriteLineAsync("  • Stop the local server and remove its service");
        await Console.Out.WriteLineAsync($"  • Delete {Path.Combine(root, "bin")} and {Path.Combine(root, "run")}");

        if (!purge) {
            await Console.Out.WriteLineAsync($"Its data in {Path.Combine(root, "data")} and its profile are kept.");
            return;
        }

        await Console.Out.WriteLineAsync($"  • Delete {Path.Combine(root, "data")} and {Path.Combine(root, "logs")}: every session it recorded");
        await Console.Out.WriteLineAsync($"  • Delete pending recordings and the caches for it in {config.Directory}");
        await Console.Out.WriteLineAsync($"  • Clear the server of every profile in {config.Directory} that points at it");
        await Console.Out.WriteLineAsync($"  • Retire {installation.ServerUrl}: nothing is sent to it again, and a new installation gets a new port");
    }

    static void Render(LocalInstallEvent e) {
        var line = e switch {
            LocalInstallEvent.Recovering r => $"Finishing an interrupted {r.Operation} (stopped at {r.Phase})…",
            LocalInstallEvent.Recovered r  => $"Finished the interrupted {r.Operation}",
            LocalInstallEvent.RollingBack r => $"Rolling back: {r.Reason}",
            LocalInstallEvent.RolledBack    => "Rolled back",
            _                               => null,
        };

        if (line is not null) Console.Out.WriteLine($"  {line}");
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
