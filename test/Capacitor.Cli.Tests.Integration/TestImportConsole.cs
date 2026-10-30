using Capacitor.Cli.Commands;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>An import terminal over the given writers, redirected so the run prints plain text and never prompts.</summary>
static class TestImportConsole {
    public static ImportConsole Over(TextWriter? stdout = null, TextWriter? stderr = null) =>
        new(stdout ?? TextWriter.Null, stderr ?? TextWriter.Null, TextReader.Null,
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) }),
            OutputRedirected: true, InputRedirected: true);
}
