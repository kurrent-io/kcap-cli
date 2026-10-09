using System.Text;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Captures what a step writes through Spectre. <c>AnsiConsole</c> caches its writer at first
/// use, so redirecting <c>Console.Out</c> never reaches it — the singleton itself has to be swapped,
/// which makes every user need bare <c>[NotInParallel]</c>.</summary>
sealed class SpectreCapture : IDisposable {
    readonly IAnsiConsole  _original = AnsiConsole.Console;
    readonly StringBuilder _text     = new();

    public SpectreCapture() {
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(new StringWriter(_text)),
        });
    }

    public string Text => _text.ToString();

    /// <summary><see cref="Text"/> with every run of whitespace collapsed to one space, since Spectre wraps
    /// at the console width and a phrase can straddle a line break.</summary>
    public string Flat => string.Join(' ', Text.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    public void Dispose() => AnsiConsole.Console = _original;
}
