using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An interactive Spectre console whose keyboard is a script, swapped in for <c>AnsiConsole.Console</c>
/// so prompts can be answered. The singleton is process-global, so every user needs bare
/// <c>[NotInParallel]</c>. A prompt that outlives the script throws rather than waiting for a key.
/// </summary>
sealed class ScriptedConsole : IAnsiConsole, IDisposable {
    readonly IAnsiConsole  _original = AnsiConsole.Console;
    readonly IAnsiConsole  _inner;
    readonly StringBuilder _text  = new();
    readonly ScriptedInput _input = new();

    public ScriptedConsole() {
        _inner = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out         = new AnsiConsoleOutput(new StringWriter(_text)),
            // Spectre's CI enrichers (GitHub Actions and friends) force a non-interactive profile
            // whatever was asked for, so a prompt that works in a terminal throws on the runner.
            Enrichment  = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        // Redirected/headless hosts can report zero terminal dimensions. Live prompt
        // layout needs a real viewport even though the output is a StringWriter.
        _inner.Profile.Width  = 120;
        _inner.Profile.Height = 40;
        _inner.Profile.Capabilities.Interactive = true;
        _inner.Profile.Capabilities.Ansi        = true;
        AnsiConsole.Console = this;
    }

    public string Text => _text.ToString();

    public ScriptedConsole Enter() => Key(ConsoleKey.Enter, '\r');

    public ScriptedConsole Down() => Key(ConsoleKey.DownArrow, '\0');

    public ScriptedConsole Line(string text) {
        foreach (var c in text) Key(char.IsDigit(c) ? ConsoleKey.D0 + (c - '0') : ConsoleKey.A, c);

        return Enter();
    }

    ScriptedConsole Key(ConsoleKey key, char c) {
        _input.Keys.Enqueue(new ConsoleKeyInfo(c, key, shift: false, alt: false, control: false));

        return this;
    }

    public Profile                 Profile         => _inner.Profile;
    public IAnsiConsoleCursor      Cursor          => _inner.Cursor;
    public IAnsiConsoleInput       Input           => _input;
    public IExclusivityMode        ExclusivityMode => _inner.ExclusivityMode;
    public RenderPipeline          Pipeline        => _inner.Pipeline;

    public void Clear(bool home)                       => _inner.Clear(home);
    public void Write(IRenderable renderable) => _inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action)   => _inner.WriteAnsi(action);

    public void Dispose() => AnsiConsole.Console = _original;

    sealed class ScriptedInput : IAnsiConsoleInput {
        public Queue<ConsoleKeyInfo> Keys { get; } = new();

        public bool IsKeyAvailable() => Keys.Count > 0;

        public ConsoleKeyInfo? ReadKey(bool intercept) =>
            Keys.TryDequeue(out var key) ? key : throw new InvalidOperationException("The prompt asked for more input than the script holds.");

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(ReadKey(intercept));
    }
}
