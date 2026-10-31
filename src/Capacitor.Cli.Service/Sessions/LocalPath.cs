using System.Diagnostics.CodeAnalysis;

namespace Capacitor.Cli.Service.Sessions;

public static class LocalPath {
    /// <summary>A drive-rooted path with no quote, so it can neither reach a share nor break out of a quoted
    /// command-line argument</summary>
    public static bool IsAbsolute([NotNullWhen(true)] string? path) =>
        path is { Length: > 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && path.IndexOf('"') < 0;
}
