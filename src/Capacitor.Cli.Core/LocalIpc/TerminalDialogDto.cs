namespace Capacitor.Cli.Core.LocalIpc;

/// A select dialog read off an agent's terminal. The dialog reads keystrokes, not numbers: an
/// answer moves the cursor from <see cref="Selected"/> to the chosen option and confirms. Options
/// are empty when the rows could not be read as one choice, and <see cref="Screen"/> is then all a
/// surface has to show. Equality is by value: a status pulse rebuilds this object, and a surface
/// must not treat an unchanged dialog as a new one.
public sealed class TerminalDialogDto : IEquatable<TerminalDialogDto> {
    public TerminalDialogDto(string heading, string body, List<string>? options, int selected, string screen) {
        Heading  = heading;
        Body     = body;
        Options  = options ?? [];
        Selected = selected;
        Screen   = screen;
    }

    public string Heading { get; }
    public string Body { get; }
    public List<string> Options { get; }
    public int Selected { get; }
    public string Screen { get; }

    public bool Equals(TerminalDialogDto? other) =>
        other is not null && Heading == other.Heading && Body == other.Body && Selected == other.Selected
     && Screen == other.Screen && Options.SequenceEqual(other.Options);

    public override bool Equals(object? obj) => Equals(obj as TerminalDialogDto);

    public override int GetHashCode() {
        var hash = HashCode.Combine(Heading, Body, Selected, Screen);
        foreach (var option in Options) hash = HashCode.Combine(hash, option);
        return hash;
    }
}
