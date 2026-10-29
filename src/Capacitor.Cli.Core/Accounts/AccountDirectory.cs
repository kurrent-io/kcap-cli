namespace Capacitor.Cli.Core.Accounts;

public static class AccountDirectory {
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(PhysicalPath.Of(path));

    /// <summary>A leading <c>~</c> or <c>~/</c> names the user's home. The shell does not expand it inside quotes
    /// or after <c>=</c>, so a command taking a directory expands it itself.</summary>
    public static string ExpandHome(string path, UserHome home) =>
        path == "~" ? home.Path
        : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home.Path, path[2..])
        : path;

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
