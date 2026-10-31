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
    // Chain workers write lines concurrently; a writer that is not the process console's has no lock.
    public TextWriter Out   { get; } = TextWriter.Synchronized(Out);
    public TextWriter Error { get; } = TextWriter.Synchronized(Error);

    public static ImportConsole FromProcess() =>
        new(Console.Out, Console.Error, Console.In, AnsiConsole.Console, Console.IsOutputRedirected, Console.IsInputRedirected);
}
