using Capacitor.Cli.Commands;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>An import terminal over buffers: plain text, stderr, and a colourless Spectre console.</summary>
sealed class TestImportTerminal {
    public StringWriter Plain    { get; } = new() { NewLine = "\n" };
    public StringWriter Error    { get; } = new() { NewLine = "\n" };
    public StringWriter Rendered { get; } = new() { NewLine = "\n" };

    public ImportConsole Console { get; }

    /// <param name="input">What a prompt reads. Null is a redirected stdin, which never prompts.</param>
    public TestImportTerminal(string? input = null) {
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(Rendered),
        });

        // Pinned, because a rule fills the width and a heading wraps against it: an unpinned layout
        // asserts whatever width the host reports.
        ansi.Profile.Width = 120;

        Console = new(Plain, Error, new StringReader(input ?? ""), ansi, OutputRedirected: true, InputRedirected: input is null);
    }

    public ImportCommand.ImportDisplay Display(bool tty = false, bool nested = false, bool quiet = false) =>
        new() { Terminal = Console, Tty = tty, Nested = nested, Quiet = quiet };
}
