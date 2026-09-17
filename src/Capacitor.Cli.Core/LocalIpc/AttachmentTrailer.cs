namespace Capacitor.Cli.Core.LocalIpc;

/// The line the daemon appends to a prompt to name delivered files. The app recognises a
/// transcript turn by it, so the shape is defined once, here.
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";

    public static string For(IEnumerable<string> paths) => Prefix + string.Join(", ", paths) + "]";
}
