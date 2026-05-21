namespace Kapacitor.Cli.Core.Cursor;

public enum OsPlatform { MacOs, Linux, Windows }

public sealed record CursorPaths(string UserDir, string WorkspaceStorageDir, string GlobalStateDb) {
    public static CursorPaths Resolve(string? home = null, OsPlatform? platform = null, string? appData = null) {
        home     ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        platform ??= OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                  :  OperatingSystem.IsWindows() ? OsPlatform.Windows
                  :                                OsPlatform.Linux;

        var sep = platform == OsPlatform.Windows ? '\\' : '/';

        var userDir = platform switch {
            OsPlatform.MacOs   => Join(sep, home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => Join(sep, appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User"),
            _                  => Join(sep, home, ".config", "Cursor", "User")
        };
        return new CursorPaths(
            UserDir:             userDir,
            WorkspaceStorageDir: userDir + sep + "workspaceStorage",
            GlobalStateDb:       userDir + sep + "globalStorage" + sep + "state.vscdb");
    }

    static string Join(char sep, string root, params string[] parts)
        => root.TrimEnd(sep) + sep + string.Join(sep, parts);
}
