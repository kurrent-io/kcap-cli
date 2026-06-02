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

    /// <summary>
    /// True when any of the OS-specific Cursor user dirs exists. Detection by
    /// directory presence — Cursor IDE users without the <c>cursor</c> shell
    /// command on PATH must still be detected (AI-730 design, Q7).
    /// </summary>
    public static bool IsInstalled(string? home = null, OsPlatform? platform = null, string? appData = null) {
        home     ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        platform ??= OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                  :  OperatingSystem.IsWindows() ? OsPlatform.Windows
                  :                                OsPlatform.Linux;

        // Universal: ~/.cursor/ (settings + hooks.json land here on every OS).
        if (Directory.Exists(Path.Combine(home, ".cursor"))) return true;

        // Per-OS Electron user dir.
        var perOs = platform switch {
            OsPlatform.MacOs   => Path.Combine(home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => Path.Combine(
                appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User"),
            _                  => Path.Combine(home, ".config", "Cursor", "User")
        };
        return Directory.Exists(perOs);
    }

    /// <summary>Path to <c>~/.cursor/hooks.json</c> — same on every OS.</summary>
    public static string UserHooksJson(string? home = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cursor", "hooks.json");
    }

    /// <summary>Hook-event spool directory at <c>~/.cursor/kapacitor-pending/</c>.</summary>
    public static string SpoolDir(string? home = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cursor", "kapacitor-pending");
    }
}
