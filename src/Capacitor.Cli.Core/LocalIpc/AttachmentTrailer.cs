namespace Capacitor.Cli.Core.LocalIpc;

/// The line the daemon appends to a prompt to name delivered files. The app recognises a
/// transcript turn by it, so the shape is defined once, here.
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";
    const string Separator = ", ";

    public static string For(IEnumerable<string> paths) => Prefix + string.Join(Separator, paths) + "]";

    /// The prompt as typed, without a trailer the daemon appended after a blank line; empty when
    /// the prompt was only files.
    public static string Strip(string text) => Split(text).Typed;

    /// The typed prompt and the paths its trailer names.
    public static (string Typed, IReadOnlyList<string> Paths) Split(string text) {
        if (IsTrailer(text)) return ("", PathsOf(text));
        var split = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (split >= 0 && IsTrailer(text[(split + 2)..])) return (text[..split].TrimEnd(), PathsOf(text[(split + 2)..]));
        return (text, []);
    }

    // The trailer has no escaping, but every file of one batch shares a directory, so ", " followed
    // by that directory separates paths where a bare ", " inside a file name does not.
    static string[] PathsOf(string trailer) {
        var body = trailer[Prefix.Length..^1];
        var comma = body.IndexOf(Separator, StringComparison.Ordinal);
        if (comma < 0) return [body];
        var slash = body.LastIndexOfAny(['/', '\\'], comma);
        if (slash < 0) return body.Split(Separator);
        var directory = body[..(slash + 1)];
        var parts = body.Split(Separator + directory);
        if (parts.Length == 1) return body.Split(Separator);
        for (var i = 1; i < parts.Length; i++) parts[i] = directory + parts[i];
        return parts;
    }

    static bool IsTrailer(string text) =>
        text.StartsWith(Prefix, StringComparison.Ordinal) && text.EndsWith(']') && !text.Contains('\n');
}
