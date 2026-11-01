using System.Globalization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>Hidden: <c>kcap local console-break &lt;pid&gt;</c>, the child a Windows stop runs to send the local server
/// Ctrl+Break. It leaves its own console to do so, so it reports only on its redirected standard error.</summary>
static class LocalConsoleBreakCommand {
    public const string Name = "console-break";

    public static int Run(string[] args) {
        // Bound now: once the console is swapped, a stream opened lazily could pick up the server's.
        var stderr = Console.Error;

        if (!OperatingSystem.IsWindows()) return Usage(stderr);
        if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return Usage(stderr);

        if (WindowsConsoleBreak.Send(pid) is not { } error) return 0;

        stderr.WriteLine(error);
        return 1;
    }

    static int Usage(TextWriter stderr) {
        stderr.WriteLine($"usage: kcap local {Name} <pid>, on Windows");
        return 2;
    }
}
