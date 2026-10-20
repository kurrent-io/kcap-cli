using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>Passes everything through to <paramref name="inner"/> and keeps the last non-blank line written.</summary>
sealed class LastLineWriter(TextWriter inner) : TextWriter {
    readonly StringBuilder _line = new();

    public string? Last { get; private set; }

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) {
        inner.Write(value);

        if (value == '\n') {
            if (_line.ToString().Trim() is { Length: > 0 } text) Last = text;

            _line.Clear();
        } else if (value != '\r') {
            _line.Append(value);
        }
    }

    public override void Flush() => inner.Flush();
}
