using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>kcap local</c> group, for the installation <c>kcap setup --local</c> made.</summary>
sealed class LocalCommand(ILocalStackFactory stacks, IBrowserLauncher browser) {
    const int DefaultLogLines = 50;

    public async Task<int> HandleAsync(string[] args) {
        var sub = args.Length > 1 ? args[1] : null;

        return sub switch {
            null or "help" => await PrintUsage(sub is null ? 1 : 0),
            "status"       => await WithStack(StatusAsync),
            "start"        => await WithStack(s => LifecycleAsync(c => c.StartAsync, s)),
            "stop"         => await WithStack(s => LifecycleAsync(c => c.StopAsync, s)),
            "restart"      => await WithStack(s => LifecycleAsync(c => c.RestartAsync, s)),
            "logs"         => await WithStack(s => LogsAsync(s, args)),
            "open"         => await WithStack(OpenAsync),
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

    static async Task<int> StatusAsync(LocalStack stack) {
        var report = await new LocalStatusReader(stack, CapacitorVersion.CurrentDisplay(), PortHolder.Find).ReadAsync(CancellationToken.None);

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
        } catch (Exception ex) when (ex is LocalInstallFailure or LocalLifecycleException) {
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
            // Read write-sharing: the server holds its logs open for its whole life.
            var all   = (await File.ReadAllTextSharedAsync(file)).Split('\n');
            var count = all.Length > 0 && all[^1].Length == 0 ? all.Length - 1 : all.Length;

            for (var i = Math.Max(0, count - lines); i < count; i++)
                await Console.Out.WriteLineAsync(all[i].TrimEnd('\r'));

            await Console.Error.WriteLineAsync($"\n--- {file} ({count} lines total) ---");
        }

        return 0;
    }

    async Task<int> OpenAsync(LocalStack stack) {
        if (stack.Read() is not LocalStackState.Installed { Installation.ServerUrl: var url, Unit: var unit })
            return await FailAsync("There is no local installation to open. kcap local status says why.");

        if (await LocalServerProbe.AuthConfigAsync(stack.Local, unit.BaseUrl, CancellationToken.None) != AuthConfigAnswer.Lifted)
            return await FailAsync($"The local server is not ready on {url}. kcap local status says why.");

        if (browser.TryOpen(url)) await Console.Out.WriteLineAsync($"  Opened {url}");
        else await Console.Out.WriteLineAsync($"  Open {url} in a browser.");

        return 0;
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
