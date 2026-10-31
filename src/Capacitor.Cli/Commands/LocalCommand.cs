using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>kcap local</c> group, for the installation <c>kcap setup --local</c> made.</summary>
sealed class LocalCommand(
    ILocalStackFactory stacks, LocalUpgradeCommand upgrade, LocalFetchCommand fetch, IBrowserLauncher browser, IOutgoingRecorders recorders,
    ConfigRoot config, UserHome home) {
    const int DefaultLogLines = 50;

    static readonly string[] UninstallFlags = ["--purge", "--force", "--yes", "-y", QuickFlag];

    const string QuickFlag = "--quick";

    /// <summary>What <c>--quick</c> gives the server to stop before its unit goes regardless. With the daemon's own step
    /// before it, an app uninstaller's 30 seconds still cover it.</summary>
    static readonly TimeSpan QuickGrace = TimeSpan.FromSeconds(10);

    public async Task<int> HandleAsync(string[] args) {
        var sub = args.Length > 1 ? args[1] : null;

        return sub switch {
            null or "help" => await PrintUsage(sub is null ? 1 : 0),
            "logs"         => await WithStack(s => LogsAsync(s, args)),
            "status" or "start" or "stop" or "restart" or "open" when args.Length > 2
                           => await FailAsync($"kcap local {sub} takes no arguments: {string.Join(' ', args[2..])}"),
            "status"       => await WithStack(StatusAsync),
            "start"        => await WithStack(s => LifecycleAsync(c => c.StartAsync, s)),
            "stop"         => await WithStack(s => LifecycleAsync(c => c.StopAsync, s)),
            "restart"      => await WithStack(s => LifecycleAsync(c => c.RestartAsync, s)),
            "open"         => await WithStack(OpenAsync),
            "uninstall"    => await UninstallAsync(args[2..]),
            "upgrade"      => await upgrade.HandleAsync(args),
            "fetch"        => await fetch.HandleAsync(args),
            LocalConsoleBreakCommand.Name => LocalConsoleBreakCommand.Run(args[2..]),
            _              => await Unknown(sub),
        };
    }

    async Task<int> WithStack(Func<LocalStack, Task<int>> run) {
        LocalStack stack;

        try {
            stack = stacks.Create();
        } catch (LocalInstallFailure failure) {
            return await FailAsync(failure.Message);
        }

        return await run(stack);
    }

    static Task<LocalStatusReport> ReadStatusAsync(LocalStack stack) =>
        new LocalStatusReader(stack, CapacitorVersion.CurrentDisplay(), PortHolder.Find).ReadAsync(CancellationToken.None);

    static async Task<int> StatusAsync(LocalStack stack) {
        var report = await ReadStatusAsync(stack);

        await Console.Out.WriteLineAsync($"  {report.Headline}");

        if (report.Fields.Count > 0) {
            await Console.Out.WriteLineAsync();
            var width = report.Fields.Max(f => f.Key.Length) + 2;

            foreach (var (key, value) in report.Fields)
                await Console.Out.WriteLineAsync($"  {key.PadRight(width)}{value}");
        }

        if (report.Notes.Count > 0) {
            await Console.Out.WriteLineAsync();
            foreach (var note in report.Notes) await Console.Out.WriteLineAsync($"  {note}");
        }

        return report.Ready ? 0 : 1;
    }

    static async Task<int> LifecycleAsync(
            Func<LocalStackControl, Func<IProgress<LocalInstallEvent>, Action<string>, CancellationToken, Task<string>>> pick, LocalStack stack) {
        var control  = new LocalStackControl(stack, PortHolder.Find);
        var progress = new SyncProgress<LocalInstallEvent>(e => {
            if (LocalSetupTerminal.Line(e, CapacitorVersion.CurrentDisplay()) is { } line) Console.Out.WriteLine($"  {line}");
        });

        try {
            var done = await pick(control)(progress, line => Console.Out.WriteLine($"  {line}"), CancellationToken.None);
            await Console.Out.WriteLineAsync($"  {done}");

            return 0;
        } catch (Exception ex) when (ex is LocalInstallFailure or LocalLifecycleException or LocalUpgradeFailure) {
            return await FailAsync(ex.Message);
        }
    }

    static async Task<int> LogsAsync(LocalStack stack, string[] args) {
        var lines = DefaultLogLines;

        for (var i = 2; i < args.Length; i++) {
            if (args[i] is "-n" or "--lines" && i + 1 < args.Length
             && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) {
                lines = n;
                i++;
            } else {
                return await FailAsync($"Unknown option for kcap local logs: {args[i]}");
            }
        }

        var files = LocalLogFiles.Current(stack.Root);

        if (files.Count == 0) return await FailAsync($"No logs in {LocalLogFiles.Directory(stack.Root)}.");

        foreach (var file in files) {
            foreach (var line in LocalLogFiles.Tail(file, lines)) await Console.Out.WriteLineAsync(line);

            await Console.Error.WriteLineAsync($"\n--- {file} ---");
        }

        return 0;
    }

    /// <summary>Opens only what status calls ready: this root's unit, running, answering as this installation.</summary>
    async Task<int> OpenAsync(LocalStack stack) {
        if (stack.Read() is not LocalStackState.Installed { Installation.ServerUrl: var url })
            return await FailAsync("There is no local installation to open. kcap local status says why.");

        if (await ReadStatusAsync(stack) is { Ready: false } report)
            return await FailAsync($"{report.Headline} kcap local status says more.");

        if (browser.TryOpen(url)) await Console.Out.WriteLineAsync($"  Opened {url}");
        else await Console.Out.WriteLineAsync($"  Open {url} in a browser.");

        return 0;
    }

    async Task<int> UninstallAsync(string[] args) {
        if (args.FirstOrDefault(a => !UninstallFlags.Contains(a)) is { } unknown) {
            await Console.Error.WriteLineAsync($"Unknown option for kcap local uninstall: {unknown}");
            return await PrintUsage(1);
        }

        var quick = args.Contains(QuickFlag);
        if (quick && args.Contains("--purge"))
            return await FailAsync($"{QuickFlag} leaves a server that is still stopping, so it cannot delete its data: drop --purge or {QuickFlag}.");

        return await WithStack(stack => UninstallAsync(stack, purge: args.Contains("--purge"), force: args.Contains("--force"),
            yes: args.Contains("--yes") || args.Contains("-y"), quick));
    }

    async Task<int> UninstallAsync(LocalStack stack, bool purge, bool force, bool yes, bool quick) {
        var root = stack.Root.Directory;

        switch (stack.Read()) {
            case LocalStackState.NotInstalled:
                await Console.Out.WriteLineAsync($"There is no local installation in {root}.");
                return 0;

            case LocalStackState.Unreadable(var path, var reason):
                return await FailAsync($"{path} cannot be read ({reason}). Nothing was changed.");

            case LocalStackState.Interrupted(var operation, _):
                await Console.Out.WriteLineAsync($"An interrupted {operation} of the local installation is finished first.");
                break;

            case LocalStackState.Installed installed:
                await DescribeUninstallAsync(installed.Installation, root, purge);
                break;
        }

        if (!yes) {
            if (Console.IsInputRedirected) return await FailAsync("pass --yes to uninstall without a prompt.");

            await Console.Out.WriteAsync("Proceed? [y/N] ");

            if (!string.Equals((await Console.In.ReadLineAsync())?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");
                return 0;
            }
        }

        var uninstall = new LocalUninstall(stack.Service, home, recorders) { QuickGrace = quick ? QuickGrace : null };

        LocalUninstallOutcome outcome;

        try {
            outcome = await stack.RunAsync(new SyncProgress<LocalInstallEvent>(e => RenderRecovery(Console.Out, e)),
                (lease, state, ct) => uninstall.RunAsync(lease, state, purge, force, config.Directory, ct), CancellationToken.None, force);
        } catch (Exception ex) when (ex is LocalLifecycleException or LocalInstallFailure or LocalUpgradeFailure) {
            return await FailAsync(ex.Message);
        }

        await Console.Out.WriteLineAsync(outcome switch {
            LocalUninstallOutcome.Purged      => $"Purged the local installation in {root}.",
            LocalUninstallOutcome.Uninstalled => $"Uninstalled the local server. Its data in {root} is kept; kcap setup --local brings it back.",
            LocalUninstallOutcome.Unloaded    => $"Removed the local server's service; the server is still stopping, so {Path.Combine(root, "bin")} and "
                                               + $"{Path.Combine(root, "run")} are left. Its data in {root} is kept; kcap setup --local brings it back.",
            _                                 => $"There is no local installation in {root}.",
        });

        return 0;
    }

    async Task DescribeUninstallAsync(LocalInstallation installation, string root, bool purge) {
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

    internal static void RenderRecovery(TextWriter output, LocalInstallEvent e) {
        var line = e switch {
            LocalInstallEvent.Recovering r  => $"Finishing an interrupted {r.Operation} (stopped at {r.Phase})…",
            LocalInstallEvent.Recovered r   => $"Finished the interrupted {r.Operation}",
            LocalInstallEvent.RollingBack r => $"Rolling back: {r.Reason}",
            LocalInstallEvent.RolledBack    => "Rolled back",
            _                               => LocalSetupTerminal.Line(e, CapacitorVersion.CurrentDisplay()),
        };

        if (line is not null) output.WriteLine($"  {line}");
    }

    static async Task<int> FailAsync(string message) {
        await Console.Error.WriteLineAsync($"  Error: {message}");

        return 1;
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
