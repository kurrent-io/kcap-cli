namespace Capacitor.Cli.Core.LocalIpc;

/// The line the daemon appends to a prompt to name delivered files. The app recognises a
/// transcript turn by it, so the shape is defined once, here.
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";

    public static string For(IEnumerable<string> paths) => Prefix + string.Join(", ", paths) + "]";

    /// The prompt as typed, without a trailer the daemon appended after a blank line; empty when
    /// the prompt was only files.
    public static string Strip(string text) {
        if (IsTrailer(text)) return "";
        var split = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        return split > 0 && IsTrailer(text[(split + 2)..]) ? text[..split].TrimEnd() : text;
    }

    static bool IsTrailer(string text) =>
        text.StartsWith(Prefix, StringComparison.Ordinal) && text.EndsWith(']') && !text.Contains('\n');
}
