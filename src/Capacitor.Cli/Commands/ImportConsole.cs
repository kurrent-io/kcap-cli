using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The terminal an import run talks to, read once so a test can hand each run its own.
/// </summary>
sealed record ImportConsole(
    TextWriter   Out,
    TextWriter   Error,
    TextReader   In,
    IAnsiConsole Ansi,
    bool         OutputRedirected,
    bool         InputRedirected
) {
    public static ImportConsole FromProcess() =>
        new(Console.Out, Console.Error, Console.In, AnsiConsole.Console, Console.IsOutputRedirected, Console.IsInputRedirected);
}
